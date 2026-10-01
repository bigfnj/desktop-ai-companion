using System;
using System.Collections.Generic;
using DesktopAICompanion.ModuleKit;
using DesktopAICompanion.Modules;
using DesktopAICompanion.Tools.ShimejiConvert;   // PetGraph.ReservedEntryPointNames, the one reserved-name array (F432)
using DesktopAICompanion.Tools.ShimejiConvert.Shimeji;   // DetectedSkin, for the multi-skin pick rule

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
            Version = "1.1.18",  // 1.1.18: the 2026-09-29 audit's PetStudio lane (BUG-012, F150-F165, F226).
                                 //         The analyzer adopts the validator's parse for the reachability
                                 //         walk and stages no sprite frame, and the window analyzes on a
                                 //         pool thread and renders on the dispatcher, so a typing pause
                                 //         no longer parses the XML twice or decodes and tiles the sheet
                                 //         on the UI thread. The capability map grows surface poses
                                 //         through flip turns and knows which surface it is on, so the
                                 //         sheep's ceiling walk and wall descent read CLIMB and its wall
                                 //         bounce does not. The zip import holds its guard through the
                                 //         extraction, refuses Open and the installed picker meanwhile,
                                 //         deletes and sweeps off the UI thread, sweeps on every load
                                 //         path and remembers the zip's folder rather than the temp tree.
                                 //         Save is offered after an installed pick, the timeline's
                                 //         dropped-step note reaches the status bar, FindBundleRoot walks
                                 //         past a folder it cannot list, and LaunchProcess is declared
                                 //         for the bundled dwebp and the optional ffmpeg.
                                 //         Lane fix/deadcode, same version: the capability map reads the
                                 //         reserved entry-point names from PetGraph, one array shared by
                                 //         the emitter, the graph and the studio (merged 2026-09-30).
                                 //         Lane burn/petstudio, same version (the Phase 8 burn-down): a
                                 //         rejected re-parse no longer empties the behaviour timeline (the
                                 //         last accepted graph stands and Resync is told when there is none);
                                 //         ENGINE follows the runtime's own binding of fall/drag/kill/sync
                                 //         instead of a case-insensitive name match; a descent reads FALL;
                                 //         the strip counts plays as the compiler does; Run chain builds its
                                 //         pet off the UI thread and a closed window drops whatever lands
                                 //         after it; a folder that cannot be remembered is said once; the
                                 //         settings pane says when the studio could not open; the zip
                                 //         import refuses before its picker; the chain self-check compares
                                 //         the originals edge for edge. Reopened 2026-10-01: a multi-skin
                                 //         archive is put to a picker instead of converting its first
                                 //         skin, and the import status carries the converter's round-trip
                                 //         diagnostic.
                                 // 1.1.17: no change in this module's OWN code. It source-links
                                 //         src/dotNet/RuntimeGeometry.cs (PetStudio.csproj:63), and
                                 //         ScalePolicy.ScaleVelocity there stopped scaling velocity
                                 //         DOWN, so a small pet is small rather than lethargic. A
                                 //         payload built before that carries the old rule and would
                                 //         preview at a different speed from the host running it,
                                 //         which is the whole reason the freshness gate compares
                                 //         commit order rather than file contents.
                                 // 1.1.16: "Preview highlighted action" plays the selected animation on the
                                 //         live desktop preview through IHost.TryPlayAnimation, so an author
                                 //         can watch a jump or a fall on demand instead of waiting for the
                                 //         pet's transition weights to pick it (this corpus reaches `jump`
                                 //         on 4 of 43 hub picks). Adds the Animation permission, which the
                                 //         module now genuinely uses. NOTE: CompanionHost does NOT gate
                                 //         TryPlayAnimation on it -- that verb takes no moduleId, so it has
                                 //         no caller identity to check -- so the declaration is honesty
                                 //         about what this module does, not what unlocked it.
                                 // 1.1.15: carries the ninth format-ladder rung. The emitter now stamps
                                 //         1.2, and `reground` upgrades a 1.1 pet in place by giving a
                                 //         non-locomotion jump an eligible border edge at a screen
                                 //         side, the screen top and a window top. A conversion done
                                 //         HERE gets 1.2 directly; the 32 shipped converted pets were
                                 //         migrated with the verb, taking the measured dead ends from
                                 //         87 to 0 on the host's own Eligible.
                                 // 1.1.14: carries the PetEmitter fix for a non-locomotion jump that had
                                 //         NO eligible border edge at a screen side, the screen top or a
                                 //         window top -- the host returned -1 and the pet walked off the
                                 //         screen and respawned. Measured: 87 such (state, situation)
                                 //         pairs across 14 shipped converted pets. This module
                                 //         source-links the engine, so a conversion done HERE gets the
                                 //         fix immediately.
                                 // 1.1.13: closing the window mid-import no longer deletes the temp
                                 //         tree the background conversion is still reading. Same
                                 //         recursive delete the second-Import guard exists for, with
                                 //         none of the guard; app exit took the same path. Adds the
                                 //         orphan sweep the comment claimed, so the deferred cleanup
                                 //         is real. Also picks up the Animations.cs magic-animation
                                 //         sentinel fix, which this module link-compiles.
                                 // 1.1.12: the last synchronous file work leaves the click handler --
                                 //         FindBundleRoot walked the whole extracted tree on the UI thread
                                 //         while its three siblings had been moved off in 1.1.9. Also
                                 //         enforces PetEmitter's single-threaded rule, which was documented
                                 //         and unchecked.
                                 // 1.1.11: a second Import while one is converting no longer deletes the first
                                 //         import's extracted files. The guard ran AFTER CleanupExtracted;
                                 //         moving the conversion off the UI thread is what made it reachable.
                                 // 1.1.10: the editor classifies the pet once per re-analyze instead of
                                 //         twice. RenderMap classified the whole pet, then the census did it
                                 //         again, on every ~750ms debounce while typing.
                                 // 1.1.9: importing a skin no longer freezes the window. The zip extraction,
                                 //        skin detection and the whole conversion ran inline from the click
                                 //        handler; they run off the UI thread now, and a second Import while
                                 //        one is converting is refused rather than corrupting the editor.
                                 // 1.1.8: a skin with no Type="Move" action no longer fails conversion. The
                                 //        synthesised `turn` was emitted unconditionally while its only inbound
                                 //        edge needed locomotion, so a hand-trimmed or single-pose skin got an
                                 //        unreachable animation and was rejected despite being playable.
                                 // 1.1.7: payload refresh, and one real conversion fix. PetEmitter.Has matched
                                 //        case-insensitively where ActionClassifier.Has used Ordinal, so an
                                 //        action merely NAMED with "Cursor" converted as a gaze: a stray
                                 //        faceCursor tag, the wrong variant rule, and a refused direction
                                 //        merge. A skin converted in this module was subject to it. Also
                                 //        deletes an unreachable roundUp branch whose doc contradicted its
                                 //        only caller.
                                 // 1.1.6: payload refresh only. WebPLoader.cs, also source-linked, now bounds
                                 //        the dwebp stdout READ rather than only the wait -- a synchronous
                                 //        CopyTo blocks until the child exits, so the 30s timeout sat after
                                 //        the hang it existed to catch. Affects any .webp sprite decode this
                                 //        module performs. No other PetStudio behaviour changed.
                                 // 1.1.5: payload refresh only. Animations.cs is one of this module's 27
                                 //        source-linked paths and gained an absenceIsNormal overload on
                                 //        SetNextBorderAnimation, so the compiled payload was behind its
                                 //        source. No PetStudio behaviour changed.
                                 // 1.1.4: two reporting defects. A MOVE animation that declares its travel on EndX
                                 //        alone rendered as "travels 0px per frame", and both behaviour-chain
                                 //        validator rejections printed with the reason discarded, because the label
                                 //        was built around the error string before the call that fills it.
                                 // 1.1.3: converter payload refresh, no behaviour change. Three source-linked
                                 //        engine files lost always-true conjuncts and an uncalled overload, and
                                 //        three uncalled members left the pane code.
                                 // 1.1.2: converter payload refresh. Sound resolution now scans the skin
                                 //        root once per clip NAME instead of once per reference, and
                                 //        remembers the clips ffmpeg refused.
                                 // 1.1.1: converter payload refresh. Five engine defects fixed (BEL byte,
                                 //        the NextBehaviour alias, drag frames drawn but never referenced,
                                 //        two ffmpeg drains that deadlocked ahead of their own timeouts).
                                 // 1.1.0: the source-linked engine gained the MIGRATION LADDER, declared once
                                 //        so a migration cannot invent its own rung. Six of the eight used to
                                 //        stamp the LATEST format instead of their own next one, so a pet at
                                 //        0.3 ran `rejump` and every later migration then skipped it while it
                                 //        still needed them. `restdwell` is retired: it inverted when the rest
                                 //        target was redefined from ~1.2s to 11s and began LENGTHENING rests,
                                 //        hub included, while printing "shortened". Nothing in this module
                                 //        calls the migrations, but it compiles PetEmitter, so the payload is
                                 //        stale without a bump.
                                 // 1.0.9: four engine fixes reach the importer. A Type="OpenURL" action is
                                 //        now REFUSED and said so in the loss report rather than reaching no
                                 //        bucket at all; a skin whose wall art does not climb keeps a
                                 //        reachable ceiling instead of failing conversion outright; the
                                 //        1024-tile cap is applied AFTER byte-identical cells collapse, so a
                                 //        sprite-duplicating skin that fits is no longer refused; and a
                                 //        set-piece chain step may not travel vertically, which is what makes
                                 //        its border edge safe rather than lucky.
                                 // 1.0.8: two ways the editor could save over the WRONG file, both of
                                 //        them atomic and so unrecoverable. Picking a pet from "Analyze
                                 //        installed companion" left _openedPath pointing at the file that
                                 //        was open, so the next Save wrote the installed pet's XML over the
                                 //        author's own and reported the victim's path as success. And Open
                                 //        adopted the new path BEFORE reading it, so a file it failed to
                                 //        read left the old content in the editor aimed at the new path.
                                 //        Also carries the engine fix that gives every class-based jump its
                                 //        landing edges, so a skin imported HERE hops instead of standing.
                                 // 1.0.7: the chain gate widened from "recovers a withheld member" to
                                 //        "a behaviour with Frequency > 0 plays it", so a skin imported
                                 //        HERE reproduces the artist's scripted ORDER and not just the
                                 //        legs that would otherwise be lost: 2 of the 13 shipped pets
                                 //        chained before, 13 of 13 now. A chain entry's hub weight is
                                 //        divided by its member count, because a weight is a selection
                                 //        probability while a Frequency describes a share of the pet's
                                 //        life, and an eight-member run picked as often as a one-shot
                                 //        Sit occupies eight times the minutes. Uncorrected that cost
                                 //        20-30 points of wall and ceiling reach on every pet.
                                 // 1.0.6: another REAL behaviour change through the source-linked
                                 //        engine. PetEmitter now converts a Sequence composite as a
                                 //        CHAIN (each member its own animation, linked at
                                 //        probability 100, only the first reachable from the hub), so
                                 //        a skin imported HERE can keep a scripted set-piece whose
                                 //        members were previously withheld -- a leg that walks off
                                 //        screen is only safe when its return leg is structurally the
                                 //        sole successor. The residue report also accounts for every
                                 //        source action now instead of going silent on a third of them.
                                 // 1.0.5: a REAL behaviour change, not just a restale. This module
                                 //        source-links the Shimeji conversion engine, so its importer
                                 //        recompiles PetEmitter.IsLocomotion, which stopped reading a
                                 //        performance that travels as locomotion. A skin imported HERE
                                 //        no longer produces a pet whose trip re-enters itself at 65%
                                 //        and stutters 2.9 times in a row. Reported from a real desktop
                                 //        on 24 of the 31 converted pets that ship.
                                 // 1.0.4: NO MODULE CHANGE. Republished because this module
                                 //        source-links src/dotNet/Animations.cs, and the pet transition
                                 //        warning there now names its pet, state and eligibility
                                 //        context. Worth knowing: editing Animations.cs, Xml.cs or any
                                 //        other host file this csproj compiles stales THIS payload, the
                                 //        same way a ModuleKit edit stales all seven.
                                 // 1.0.2: republished so the bundled ModuleKit.dll no longer carries the
                                 //        maintainer's absolute build path (Contracts + ModuleKit moved to
                                 //        DebugType=embedded). NO functional change here; the bump exists
                                 //        because the catalog offers an update by VERSION, so without it the
                                 //        cleaned payload would only ever reach new installs.
                                 // 1.0.1: picked up a source-linked RuntimeGeometry change (the host gained
                                 //        SelectCompanionMonitor for BUG-003(b)). This module compiles that
                                 //        file in, so its payload went stale the moment the host changed it
                                 //        and CI fails until it is republished -- see docs/VERSIONING.md,
                                 //        which calls this the most common reason a module number moves.
                                 //        MinHostVersion deliberately stays 1.0.0: nothing here calls a
                                 //        newly introduced ABI member, and raising the floor would stop the
                                 //        module loading on hosts that can run it perfectly well.
                                 // 1.0.0: rebased with the host for the Desktop AI Companion rename. Not a
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
            // Speech added 2026-09-17, found by an ABI audit. Only the two error paths use it
            // (failing to open the window, failing to start an import), which is a smaller blast
            // radius than Remembrance's but the same defect: the companion speaks for a module whose
            // consent line said it does not.
            // LaunchProcess: an Android-bundle import runs the bundled dwebp.exe once per .webp sprite
            // (WebPLoader, source-linked), and a skin with sounds runs ffmpeg and ffprobe when they are on
            // PATH (SoundBaker in Engine.cs). The flag gates nothing at runtime -- the module spawns them
            // itself -- so this line is disclosure on the consent screen and the "wants:" row, and the
            // module self-test is the only thing that notices it gone (F226; AiBrain and Remembrance
            // declared theirs the same day, for ollama and whisper-cli).
            Permissions = ModulePermissions.Speech | ModulePermissions.Animation
                          | ModulePermissions.Companions | ModulePermissions.Storage
                          | ModulePermissions.LaunchProcess,
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
            string failureCategory;
            return System.Threading.Tasks.Task.FromResult(OpenStatus(TryOpen(out failureCategory), failureCategory));
        }

        /// <summary>
        /// The status the settings pane shows beside "Open Companion Studio…" (PaneAction.InvokeAsync's
        /// result). Until 1.1.18 it was the constant success text whatever <see cref="TryOpen"/> caught, so a
        /// window whose construction failed left the pane saying the studio was open (RA-138). The bubble and
        /// the log line ReportFailure writes are unchanged; this is the third report, on the surface the user
        /// pressed. Pure, so the module self-test pins it.
        /// </summary>
        internal static string OpenStatus(bool opened, string failureCategory)
        {
            if (opened) return "Companion Studio is open.";
            return "Couldn't open Companion Studio: " + (string.IsNullOrEmpty(failureCategory) ? "unknown" : failureCategory) +
                   " (the diagnostic log has this line; the full message was spoken).";
        }

        /// <summary>Show the studio, or bring the existing one forward, for the tray entry and the import
        /// deep-link, which have no status line to hand the outcome to.</summary>
        private void Open()
        {
            string ignored;
            TryOpen(out ignored);
        }

        /// <summary>Show the studio, or bring the existing one forward. One window: a second would let two
        /// previews fight over the same pet slots. False, with the failure's category, when the window could not
        /// be constructed or shown; the failure is also reported the two usual ways.</summary>
        private bool TryOpen(out string failureCategory)
        {
            failureCategory = null;
            try
            {
                if (_window != null && _window.IsLoaded)
                {
                    _window.Activate();
                    return true;
                }
                _window = new PetStudioWindow(_host);
                _window.Closed += delegate { _window = null; };
                _window.Show();
                return true;
            }
            catch (Exception ex)
            {
                failureCategory = Categorize(ex);
                ReportFailure("could not open", ex);
                return false;
            }
        }

        /// <summary>
        /// Report a failure both ways: a bubble for the user, and a line in the diagnostic log.
        ///
        /// THE LOG LINE IS NOT REDUNDANT WITH THE BUBBLE, and it is the only reason this module logs at all.
        /// <c>IHost.SayAll</c> reaches <c>StartUp.ShowBubbleOnAll</c>, which asks <c>DefaultSpeaker()</c> for
        /// a companion to speak through and silently DROPS the message when that returns null — i.e. whenever
        /// no companion is on screen. Both entry points into this module are reachable in exactly that
        /// state: the tray entry is always present, and the host's Companions pane deep-links
        /// <see cref="OpenForImport"/>. So "Companion Studio does nothing when I click it" was a report with
        /// no evidence anywhere, which is the same defect the host fixed in 1.4.8 for a module that fails to
        /// LOAD.
        ///
        /// Everything else this module can get wrong is reported by the window's own status bar to a user who
        /// is looking straight at it, which is why there is one line here and not a set.
        ///
        /// The bubble keeps the exception MESSAGE — the user is entitled to it on their own screen — and the
        /// log gets a category only, because a WPF or IO failure here names a path inside their profile.
        /// Internal so the module's self-check can drive it without a window.
        /// </summary>
        internal void ReportFailure(string what, Exception ex)
        {
            IHost host = _host;
            if (host == null) return;
            // Log first: it is the report that survives a speech path that has nobody to speak through, and
            // neither call is allowed to throw into the host.
            try { host.Log(Info.Id, what + ": " + Categorize(ex)); } catch { }
            try { host.SayAll("Companion Studio " + what + ": " + (ex == null ? "unknown error" : ex.Message)); }
            catch { }
        }

        /// <summary>
        /// A swallowed exception as a short, non-identifying category, following
        /// <c>AiBrain.DescribeError</c>. The message is dropped on purpose: the failures reachable here are
        /// WPF construction and file IO, whose messages quote the path they failed on.
        /// </summary>
        private static string Categorize(Exception ex)
        {
            if (ex == null) return "none";
            if (ex is UnauthorizedAccessException) return "access-denied";
            if (ex is System.IO.FileNotFoundException) return "assembly-or-file-missing";
            if (ex is System.IO.IOException) return "io";
            if (ex is InvalidOperationException) return "invalid-state";
            if (ex is TypeInitializationException) return "type-init";
            return ex.GetType().Name;
        }

        /// <summary>Open the studio (or bring it forward) and immediately start the Shimeji import flow. Public
        /// so the host's Pets pane can deep-link straight here, invoked by reflection over the loaded module
        /// instance (the host cannot cast across the module's load context, and IModule stays frozen).</summary>
        public void OpenForImport()
        {
            Open();
            try { if (_window != null) _window.BeginImport(); }
            catch (Exception ex) { ReportFailure("import could not start", ex); }
        }

        public void Shutdown()
        {
            PetStudioWindow window = _window;
            _window = null;
            if (window == null) return;
            // Closing removes any live preview: the window owns that handle.
            try { window.Close(); } catch { }
        }

        /// <summary>
        /// The module's own self-test, reached by <c>--module-selftest=petstudio</c> (ModuleConventionSelfTest
        /// finds this exact shape by reflection) and listed as COVERED in tests/Test-ModuleSelfTests.ps1.
        ///
        /// The in-module checks (<see cref="BehaviourChainSelfCheck"/>, <see cref="AnimCapabilitySelfCheck"/>)
        /// need a pet to analyse. The host's <c>--petstudio-selftest</c> hands them its bundled pet; a module
        /// cannot reference the host's resources, so this one ships its own: Resources/selftest-companion.xml,
        /// the bundled eSheep GRAPH (54 animations, every edge kind, flagged border edges) with the 640x760
        /// sprite sheet replaced by a generated 64x44 placeholder (16x11 cells, the Magenta colour key the XML
        /// declares, one opaque block per cell). The graph is what the checks read; the art only has to
        /// decode. 42 KB instead of 158 KB. Rejected: reading Companions/ off disk (a self-test must not
        /// depend on the working directory), and a hand-written stub pet (the chain checks want a real graph
        /// with both natural and forced joins, and the bundled one is exactly that).
        ///
        /// The ONLY <c>SelfTest</c> in this assembly, by design: the host invokes the first
        /// <c>public static bool SelfTest(out string)</c> it finds and reports an ambiguity if there are two,
        /// which is why the two check classes are named RunChecks.
        /// </summary>
        public static bool SelfTest(out string detail)
        {
            var probe = new SelfTestProbe();
            try
            {
                var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                var module = new PetStudioModule();
                module.Init(host);

                probe.Check("contributes exactly one tray entry", host.TrayItems.Count == 1);
                probe.Check("the tray entry ships an icon (embedded PNG resolves)",
                    host.TrayItems.Count == 1 && host.TrayItems[0].IconPng != null && host.TrayItems[0].IconPng.Length > 0);
                probe.Check("contributes exactly one settings pane", host.OptionsPanes.Count == 1);
                // The ACTION, not the array: the studio must be openable from the settings window.
                PaneAction openStudio = null;
                if (host.OptionsPanes.Count == 1 && host.OptionsPanes[0].Actions != null)
                    foreach (PaneAction action in host.OptionsPanes[0].Actions)
                        if (action != null && action.Label != null
                            && action.Label.StartsWith("Open Companion Studio", StringComparison.Ordinal))
                            openStudio = action;
                probe.Check("opening the studio is offered as a pane action (labelled, with an InvokeAsync)",
                    openStudio != null && openStudio.InvokeAsync != null);
                probe.Check("declares Speech, Animation, Companions, Storage and LaunchProcess (F226: it spawns dwebp, and ffmpeg when present)",
                    module.Info.Permissions.HasFlag(ModulePermissions.Speech)
                    && module.Info.Permissions.HasFlag(ModulePermissions.Animation)
                    && module.Info.Permissions.HasFlag(ModulePermissions.Companions)
                    && module.Info.Permissions.HasFlag(ModulePermissions.Storage)
                    && module.Info.Permissions.HasFlag(ModulePermissions.LaunchProcess));
                probe.Check("WITNESS the declaration is not a blanket: Microphone, which nothing here uses, is not declared",
                    !module.Info.Permissions.HasFlag(ModulePermissions.Microphone));
                probe.Check("says and logs nothing at startup", host.SaidLines.Count == 0 && host.LoggedLines.Count == 0);

                // ReportFailure: the log line is the report that survives a speech path with nobody to speak
                // through, and it carries a CATEGORY rather than the message, which for the failures reachable
                // here quotes a path inside the user's profile. The bubble keeps the message: it is the user's
                // own screen.
                module.ReportFailure("could not open", new System.IO.FileNotFoundException(@"C:\Users\someone\secret-pet.xml"));
                probe.Check("a failure is logged under the module id as a category",
                    host.LoggedLines.Count == 1 && host.LoggedLines[0] == "petstudio: could not open: assembly-or-file-missing");
                probe.Check("WITNESS the log line does not quote the path the exception carried",
                    host.LoggedLines.Count == 1 && host.LoggedLines[0].IndexOf("secret-pet", StringComparison.Ordinal) < 0);
                probe.Check("the same failure is spoken to the user with its message",
                    host.SaidLines.Count == 1
                    && host.SaidLines[0].StartsWith("Companion Studio could not open: ", StringComparison.Ordinal)
                    && host.SaidLines[0].IndexOf("secret-pet.xml", StringComparison.Ordinal) >= 0);

                // The other reachable failure shape, and the one whose message quotes a PROFILE path: a WPF
                // construction failure. The category reaches the log and the path does not; the equality is
                // what makes the three absence tests mean something, since each passes on an empty log, the
                // one state they must not certify. This check lived in BehaviourChainSelfCheck until 1.1.18,
                // placed there because no module SelfTest existed and the gate ran none; both premises went in
                // the 2026-09-29 campaign, and it then ran twice per --module-selftest=petstudio (RA-134).
                host.ClearLoggedLines();
                host.ClearSaidLines();
                module.ReportFailure("could not open", new InvalidOperationException(
                    @"C:\Users\someone\AppData\Local\Whatever\petstudio.xaml is not a valid resource"));
                string constructionLine = host.LoggedLines.Count == 1 ? host.LoggedLines[0] : "";
                probe.Check("a window-construction failure is logged as its category, and the profile path its message carries does NOT reach the log",
                    constructionLine == "petstudio: could not open: invalid-state"
                    && constructionLine.IndexOf("AppData", StringComparison.OrdinalIgnoreCase) < 0
                    && constructionLine.IndexOf("someone", StringComparison.OrdinalIgnoreCase) < 0
                    && constructionLine.IndexOf(":\\", StringComparison.Ordinal) < 0);
                probe.Check("WITNESS the same failure is still spoken with its message, path and all, on the user's own screen",
                    host.BroadcastLines.Count == 1
                    && host.BroadcastLines[0].IndexOf("petstudio.xaml", StringComparison.Ordinal) >= 0);

                // RA-138: the pane's status beside "Open Companion Studio…" reports a failed open; it was the
                // constant success text whatever the open caught.
                string failedOpen = PetStudioModule.OpenStatus(false, "invalid-state");
                probe.Check("a failed open is not reported to the pane as open: the status names the category (RA-138)",
                    failedOpen != PetStudioModule.OpenStatus(true, null)
                    && failedOpen.IndexOf("Couldn't open", StringComparison.Ordinal) >= 0
                    && failedOpen.IndexOf("invalid-state", StringComparison.Ordinal) >= 0);
                probe.Check("WITNESS the success status is unchanged",
                    PetStudioModule.OpenStatus(true, null) == "Companion Studio is open.");

                // RA-141: a folder that cannot be remembered is said, once per window, instead of Save()'s false
                // being discarded behind a catch the shipped host cannot trigger.
                var unwritable = new DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings { FailSaves = true };
                var folderHost = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                bool said = false;
                PetStudioWindow.RememberFolder(unwritable, folderHost, PetStudioPaths.LastOpenDirKey, @"C:\pets\wip", ref said);
                PetStudioWindow.RememberFolder(unwritable, folderHost, PetStudioWindow.LastSkinDirKey, @"C:\skins", ref said);
                probe.Check("a folder that cannot be remembered is said once in the log, not on every dialog (RA-141)",
                    said && unwritable.SaveCount == 2 && folderHost.LoggedLines.Count == 1
                    && folderHost.LoggedLines[0].StartsWith("petstudio: last folder not remembered", StringComparison.Ordinal));
                var writable = new DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings();
                var quietHost = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                bool unsaid = false;
                PetStudioWindow.RememberFolder(writable, quietHost, PetStudioPaths.LastOpenDirKey, @"C:\pets\wip", ref unsaid);
                probe.Check("WITNESS a folder that saves is stored under its key and nothing is logged",
                    !unsaid && writable.Get(PetStudioPaths.LastOpenDirKey, "") == @"C:\pets\wip" && quietHost.LoggedLines.Count == 0);

                string fixture = EmbeddedResources.LoadText(typeof(PetStudioModule).Assembly, "selftest-companion.xml");
                if (!probe.Check("the self-test companion is embedded (" + fixture.Length + " chars)", fixture.Length > 1000))
                    return probe.Finish(out detail);
                PetReport report = PetAnalyzer.Analyze(fixture);
                probe.Check("WITNESS the fixture analyses as a valid pet carrying the bundled graph ("
                            + report.Nodes.Count + " animations)",
                    report.IsValid && report.Nodes.Count >= 50);

                // BUG-012 (F155): the reachability stage adopts the validator's parse and touches no sprite.
                // Both facts are recorded by Analyze itself, so this asserts what the shipped path DID, not
                // what its comment says it does.
                probe.Check("the analyzer's reachability stage decodes no sprite frame (BUG-012: it tiled the whole sheet per analyze)",
                    report.StagedSpriteFrames == 0);
                probe.Check("the analyzer's reachability stage adopts the validator's parsed graph rather than parsing the XML a second time",
                    report.StagedFromParsedGraph);
                probe.Check("WITNESS the fixture's sheet has tiles to decode (" + (report.TilesX * report.TilesY)
                            + "), so a staged frame count of 0 is a choice and not an empty sheet",
                    report.TilesX * report.TilesY > 1);
                // The names come from the one array (F432), so a fifth entry point is asserted the day it exists
                // rather than silently uncounted (RA-139); the fixture spells all four exactly, which is the
                // one rule the loader binds by.
                bool entriesAreRoots = true;
                int entriesSeen = 0;
                foreach (AnimNode n in report.Nodes)
                    foreach (string magic in PetGraph.ReservedEntryPointNames)
                        if (string.Equals(n.Name, magic, StringComparison.Ordinal))
                        {
                            entriesSeen++;
                            if (!n.IsRoot || !n.IsEngineEntry) entriesAreRoots = false;
                        }
                probe.Check("WITNESS the staged graph still resolves the engine's entry animations: the fixture's "
                            + string.Join(", ", PetGraph.ReservedEntryPointNames) + " are roots and engine entries (" + entriesSeen + " found)",
                    entriesSeen == PetGraph.ReservedEntryPointNames.Length && entriesAreRoots);

                // F165 and the F429 wording: the status sentences are pure, so their words can be pinned here.
                probe.Check("the analysis status carries the timeline's dropped-step note (F165)",
                    PetStudioWindow.AnalysisStatus(true, 0, 2).IndexOf("Dropped 2 timeline step(s)", StringComparison.Ordinal) >= 0);
                probe.Check("WITNESS nothing dropped, nothing said: the plain verdict is unchanged",
                    PetStudioWindow.AnalysisStatus(true, 0, 0) == "This companion is good to go.");
                probe.Check("the verdict still names the never-play count and the rejection",
                    PetStudioWindow.AnalysisStatus(true, 3, 0).IndexOf("3 animation(s) will never play", StringComparison.Ordinal) >= 0
                    && PetStudioWindow.AnalysisStatus(false, 5, 0) == "The host would reject this companion.");
                probe.Check("an imported pet the validator accepted is not announced as one the host would reject (F429)",
                    PetStudioWindow.ImportedStatusPrefix("hornet", true, true, "", "") == "Imported 'hornet'. ");
                probe.Check("WITNESS the one converter fact the analysis cannot see is still said: a pet whose XML does not round-trip",
                    PetStudioWindow.ImportedStatusPrefix("hornet", true, false, "", "").IndexOf("does not round-trip", StringComparison.Ordinal) >= 0);
                // N-burn-tools-02 (RA-372's PetStudio half): the converter's first-difference diagnostic for a
                // pet that does not round-trip (F427, carried in ConversionResult.Error) reaches the import
                // status; until 1.1.18 the status appended the clause alone and the diagnostic reached nobody.
                probe.Check("F427's first-difference diagnostic reaches the import status of a pet that does not round-trip (N-burn-tools-02)",
                    PetStudioWindow.ImportedStatusPrefix("hornet", true, false, "",
                            "the emitted XML is not a fixed point of parse -> serialize: at offset 6944 of 30923/32203 chars")
                        .IndexOf("at offset 6944 of 30923/32203 chars", StringComparison.Ordinal) >= 0);
                probe.Check("WITNESS a pet that round-trips shows no diagnostic, whatever Error holds",
                    PetStudioWindow.ImportedStatusPrefix("hornet", true, true, "", "stale text") == "Imported 'hornet'. ");

                // N-burn-tools-01: a multi-skin archive is the author's pick, not skins[0]. The rule is driven
                // with a recording picker; the modal list itself is UI a headless self-test cannot reach.
                var solo = new DetectedSkin { Name = "solo" };
                int asked = 0;
                Func<IReadOnlyList<DetectedSkin>, DetectedSkin> recordingPicker = delegate (IReadOnlyList<DetectedSkin> offered)
                {
                    asked++;
                    return offered[1];
                };
                probe.Check("WITNESS a single skin converts without asking",
                    ReferenceEquals(PetStudioWindow.SelectSkin(new[] { solo }, recordingPicker), solo) && asked == 0);
                var three = new[] { new DetectedSkin { Name = "a" }, new DetectedSkin { Name = "b" }, new DetectedSkin { Name = "c" } };
                probe.Check("a multi-skin archive is put to the picker and its choice is converted, not skins[0] (N-burn-tools-01)",
                    ReferenceEquals(PetStudioWindow.SelectSkin(three, recordingPicker), three[1]) && asked == 1);
                probe.Check("WITNESS a cancelled pick converts nothing",
                    PetStudioWindow.SelectSkin(three, delegate (IReadOnlyList<DetectedSkin> offered) { return null; }) == null);

                // F164: FindBundleRoot survives a folder it cannot list. Its two probes are injected, so no ACL
                // games: the lister throws for one subfolder that sorts ahead of the bundle, as a denied folder
                // did on the real enumerator, and the bundle after it must still be found. The bundle sits TWO
                // levels down, as in the finding's scenario, and that is load-bearing: a direct child is found
                // while the root's own listing is read, before the denied sibling is ever listed, so a fixture
                // with the bundle at the top never reached the catch -- the mutation harness scored the first
                // draft of this check SURVIVED for exactly that reason.
                var tree = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                {
                    { @"R", new[] { @"R\a-first", @"R\m-denied", @"R\z-deep" } },
                    { @"R\a-first", new string[0] },
                    { @"R\z-deep", new[] { @"R\z-deep\bundle" } },
                    { @"R\z-deep\bundle", new string[0] },
                };
                Func<string, IEnumerable<string>> listing = delegate (string dir)
                {
                    string[] subs;
                    return tree.TryGetValue(dir, out subs) ? subs : new string[0];
                };
                Func<string, IEnumerable<string>> denying = delegate (string dir)
                {
                    if (string.Equals(dir, @"R\m-denied", StringComparison.OrdinalIgnoreCase))
                        throw new UnauthorizedAccessException("Access to the path 'R\\m-denied' is denied.");
                    return listing(dir);
                };
                Func<string, bool> isBundle = delegate (string dir)
                {
                    return string.Equals(dir, @"R\z-deep\bundle", StringComparison.OrdinalIgnoreCase);
                };
                probe.Check("FindBundleRoot keeps walking past a subfolder it cannot list and finds the bundle two levels down behind it (F164)",
                    PetStudioWindow.FindBundleRoot("R", denying, isBundle) == @"R\z-deep\bundle");
                probe.Check("WITNESS the same tree with nothing denied finds the same bundle",
                    PetStudioWindow.FindBundleRoot("R", listing, isBundle) == @"R\z-deep\bundle");
                probe.Check("WITNESS a tree with no bundle answers null, not a folder",
                    PetStudioWindow.FindBundleRoot("R", denying, delegate (string dir) { return false; }) == null);
                probe.Check("WITNESS a root that is itself the bundle is answered without listing anything",
                    PetStudioWindow.FindBundleRoot("R",
                        delegate (string dir) { throw new InvalidOperationException("the walk must not list when the root is the bundle"); },
                        delegate (string dir) { return dir == "R"; }) == "R");

                RunChecks(probe, "BehaviourChainSelfCheck", BehaviourChainSelfCheck.RunChecks, fixture,
                    "the chain builder's verdicts hold on the fixture pet");
                RunChecks(probe, "AnimCapabilitySelfCheck", AnimCapabilitySelfCheck.RunChecks, fixture,
                    "the capability map reports what each animation DOES, not just its name");

                module.Shutdown();
            }
            catch (Exception ex) { probe.Exception(ex); }
            return probe.Finish(out detail);
        }

        private delegate bool ChecksRunner(string fixturePetXml, out string detail);

        /// <summary>Run one RunChecks entry point and fold its verdict in. Its passing lines are echoed as
        /// notes; each FAILING line becomes a FAIL of its own, so the report (and tests/mutate-selftest-guards.py,
        /// which reads column-0 FAIL lines) names the assertion that fell rather than only the group.</summary>
        private static void RunChecks(SelfTestProbe probe, string name, ChecksRunner run, string fixturePetXml, string verdict)
        {
            string lines;
            bool ok = run(fixturePetXml, out lines);
            foreach (string raw in (lines ?? "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("FAIL ", StringComparison.Ordinal))
                    probe.Check("[" + name + "] " + line.Substring(5).Trim(), false);
                else if (line.StartsWith("EXC ", StringComparison.Ordinal))
                    probe.Check("[" + name + "] " + line, false);
                else
                    probe.Note("[" + name + "] " + line);
            }
            probe.Check(verdict, ok);
        }
    }
}
