using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using DesktopAICompanion.Ai;
using DesktopAICompanion.ModuleKit.Testing;   // RecordingHost
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.AiBrainModule
{
    // The fullscreen stand-down's two questions (aibrain 1.4.0, lane feature/fullscreen-per-monitor). The owner, 2026-10-07:
    // Ctrl+Alt+P did nothing on Claude Code while a game ran on ANOTHER monitor, then "if it's on a monitor that does NOT
    // have a fullscreen app it is not suppressed, if it's on a fullscreen in-use monitor it auto-suppresses". Every check
    // here names which of the two cases it drives: the companion moved to a free monitor (in view), or hidden with nowhere
    // to go (one monitor, or a pinned companion).
    public static partial class AiEngineProbe
    {
        /// <summary>
        /// A RecordingHost that also answers ICompanionStandDown (host 1.5.0) the way the host decides it: a companion is
        /// stood down while something is fullscreen, unless the host's stand-down moved it to a free monitor
        /// (<see cref="MovedToFreeMonitor"/>). The default is the one-monitor case, where a game hides every companion,
        /// so each check written before 1.4.0 keeps the meaning it had; a check about a second monitor says so by adding
        /// the companion.
        /// </summary>
        internal sealed class StandDownHost : RecordingHost, ICompanionStandDown
        {
            internal readonly HashSet<ICompanion> MovedToFreeMonitor = new HashSet<ICompanion>();

            public bool IsCompanionStoodDown(ICompanion pet)
            {
                return pet != null && IsFullscreenActive && !MovedToFreeMonitor.Contains(pet);
            }
        }

        private const string StoodDownDecline = "ask declined: companion stood down for a fullscreen window";

        /// <summary>On a CLI: a game on another monitor stops nothing, a hidden companion is not asked, and nothing about
        /// a graphics card is ever said, since a CLI loads nothing on this machine.</summary>
        private static bool CheckFullscreenOnCli(StringBuilder sb)
        {
            bool ok = true;
            using (var rig = new CliRig("aibrain-fullscreen-cli", ", \"CliBackend\": \"claude\""))
            {
                ok &= Check(sb, "fullscreen cli rig: the live brain is built",
                    SpinWait.SpinUntil(delegate { return rig.Module.SessionForDiagnostics.LiveBrainForDiagnostics != null; },
                        TimeSpan.FromSeconds(5)));

                // A game on ANOTHER monitor: the companion moved to a free one and is in view. The owner's case.
                rig.Host.MovedToFreeMonitor.Add(rig.Pet);
                rig.Host.RaiseFullscreenChanged(true);
                int declined = CountDeclined(rig.Host);
                bool drop = rig.Host.RaiseDrop(rig.Pet);
                bool poke = rig.Host.RaisePokeResponders(rig.Pet);
                bool tray = ClickTray(rig.Host, "Ask about my screen");
                ok &= Check(sb, "fullscreen cli: with a game on another monitor the drop, the poke and the tray ask all start their turn for a companion in view",
                    drop && poke && tray && rig.Started == 3 && CountDeclined(rig.Host) == declined);
                ok &= Check(sb, "fullscreen cli: ...and nothing about the graphics card is said, since a CLI loads nothing here",
                    !rig.Host.SaidLines.Contains(AiBrainModule.FullscreenGpuSpokenLine));

                // One monitor: the companion hid with nowhere to go.
                rig.Host.MovedToFreeMonitor.Clear();
                int before = rig.Started;
                int said = rig.Host.SaidLines.Count;
                bool offered = ClickTray(rig.Host, "Ask about my screen");
                ok &= Check(sb, "fullscreen cli: for a companion hidden behind a game no turn starts, since the host would hold its answer until the game ends",
                    offered && rig.Started == before);
                ok &= Check(sb, "fullscreen cli: ...the refusal is logged under its own category, and nothing is said to the hidden companion",
                    CountDeclined(rig.Host) == declined + 1 &&
                    rig.Host.LoggedLines.Exists(delegate(string l) { return l.Contains(StoodDownDecline); }) &&
                    rig.Host.SaidLines.Count == said);
                ok &= Check(sb, "fullscreen cli: the poke for a hidden companion is declined too, silently, so Fortunes answers",
                    !rig.Host.RaisePokeResponders(rig.Pet) && rig.Started == before && CountDeclined(rig.Host) == declined + 1);

                rig.Host.RaiseFullscreenChanged(false);
                ok &= Check(sb, "WITNESS fullscreen cli: once the game closes the same tray ask starts a turn",
                    ClickTray(rig.Host, "Ask about my screen") && rig.Started == before + 1);
            }
            return ok;
        }

        /// <summary>On the local model: the graphics-card guard declines while a game runs on ANY monitor, releases the
        /// model, says why to a companion in view and nothing to a hidden one; the switch governs the guard and nothing
        /// else, so a hidden companion is not asked with it off either.</summary>
        private static bool CheckFullscreenOnLocal(StringBuilder sb)
        {
            bool ok = true;
            using (var rig = new StandDownRig("aibrain-fullscreen-local"))
            {
                ok &= Check(sb, "fullscreen local rig: the live brain is built over the counting double", rig.WaitForLiveBrain());

                // A game on another monitor, the companion in view beside it.
                rig.Host.MovedToFreeMonitor.Add(rig.Pet);
                int unloads = rig.Backend.UnloadCalls;
                rig.Host.RaiseFullscreenChanged(true);
                ok &= Check(sb, "fullscreen local: a game starting on another monitor still releases the local model",
                    rig.Backend.UnloadCalls == unloads + 1);
                ok &= Check(sb, "fullscreen local: with a game on another monitor the drop and the poke are declined silently, so Fortunes answers",
                    !rig.Host.RaiseDrop(rig.Pet) && !rig.Host.RaisePokeResponders(rig.Pet) && rig.Started.Count == 0 &&
                    rig.Host.SaidLines.Count == 0);
                bool offered = ClickTray(rig.Host, "Ask about my screen");
                ok &= Check(sb, "fullscreen local: the tray ask is declined on the local model while a game runs on any monitor, and logged",
                    offered && rig.Started.Count == 0 && rig.CountLog("ask declined: fullscreen stand-down") == 1);
                ok &= Check(sb, "fullscreen local: ...and SAYS why, once, to the companion in view beside the game",
                    rig.SaidTo(rig.Pet, AiBrainModule.FullscreenGpuSpokenLine) == 1 && rig.Host.SaidLines.Count == 1);

                // One monitor: the same refusal, unsaid.
                rig.Host.MovedToFreeMonitor.Clear();
                ClickTray(rig.Host, "Ask about my screen");
                ok &= Check(sb, "fullscreen local: for a hidden companion the refusal is logged and NOT said, since the host would hold the line past the game",
                    rig.Started.Count == 0 && rig.CountLog("ask declined: fullscreen stand-down") == 2 && rig.Host.SaidLines.Count == 1);

                // The switch governs the graphics-card guard and nothing else.
                AiSettings settings = rig.Module.SettingsForDiagnostics;
                settings.StandDownForFullscreen = false;
                try
                {
                    rig.Host.MovedToFreeMonitor.Add(rig.Pet);
                    ok &= Check(sb, "WITNESS fullscreen local: with the switch off a game on another monitor no longer stops the tray ask",
                        ClickTray(rig.Host, "Ask about my screen") && rig.Started.Count == 1);
                    rig.Host.MovedToFreeMonitor.Clear();
                    ClickTray(rig.Host, "Ask about my screen");
                    ok &= Check(sb, "fullscreen local: with the switch off a hidden companion is still not asked, since being seen is no preference",
                        rig.Started.Count == 1 && rig.CountLog(StoodDownDecline) == 1);
                }
                finally { settings.StandDownForFullscreen = true; }
                rig.Host.RaiseFullscreenChanged(false);
            }
            return ok;
        }

        /// <summary>On a cloud slot: a game on another monitor stops no turn, and the cloud's fallback to the local model is
        /// held back for as long as the game runs, the log naming the game; the switch off holds nothing.</summary>
        private static bool CheckFullscreenCloudFallback(StringBuilder sb)
        {
            bool ok = true;
            const string CloudSeed = ", \"Provider\": \"openai\", \"OpenAiBaseUrl\": \"https://api.openai.com/v1\", " +
                                     "\"CloudDataConsent\": true, \"CloudTextModel\": \"gpt-test-model\"";
            const string GameHold = "a fullscreen app is running, and the local model would load beside it";
            using (var rig = new StandDownRig("aibrain-fullscreen-cloud", extraSeed: CloudSeed, liveBrainSeam: false))
            {
                ok &= Check(sb, "fullscreen rig (cloud): the live brain is built from the settings, as shipped", rig.WaitForLiveBrain());
                AiBrain live = rig.Module.SessionForDiagnostics.LiveBrainForDiagnostics;
                FallbackBackend composite = live == null ? null : live.BackendForDiagnostics as FallbackBackend;
                ok &= Check(sb, "WITNESS fullscreen cloud: with no game the composite's fallback is allowed",
                    composite != null && composite.LocalFallbackHold != null && composite.LocalFallbackHold() == null);

                rig.Host.MovedToFreeMonitor.Add(rig.Pet);
                rig.Host.RaiseFullscreenChanged(true);
                ok &= Check(sb, "fullscreen cloud: a game starting holds the cloud's fallback to the local model back at once, naming the game",
                    composite != null && composite.LocalFallbackHold() == GameHold);
                ok &= Check(sb, "fullscreen cloud: with a game on another monitor the drop and the tray ask go ahead on the cloud",
                    rig.Host.RaiseDrop(rig.Pet) && ClickTray(rig.Host, "Ask about my screen") && rig.Started.Count == 2 &&
                    rig.CountLog("ask declined") == 0);
                ok &= Check(sb, "fullscreen cloud: ...and each decision's own reading keeps the fallback held back",
                    rig.Module.LocalFallbackHoldForDiagnostics() == GameHold);

                rig.Host.RaiseFullscreenChanged(false);
                ok &= Check(sb, "WITNESS fullscreen cloud: once the game closes the fallback is allowed again",
                    composite != null && composite.LocalFallbackHold() == null);

                AiSettings settings = rig.Module.SettingsForDiagnostics;
                settings.StandDownForFullscreen = false;
                try
                {
                    rig.Host.RaiseFullscreenChanged(true);
                    ok &= Check(sb, "fullscreen cloud: with the switch off a game holds nothing back",
                        composite != null && composite.LocalFallbackHold() == null);
                    rig.Host.RaiseFullscreenChanged(false);
                }
                finally { settings.StandDownForFullscreen = true; }
            }
            return ok;
        }

        /// <summary>Remembrance's spoken refusal follows the same rule: said to a companion in view, never to one hidden
        /// behind a game, whose held line would play after the game as an answer to nothing. The fullscreen switch is off
        /// so the graphics-card guard steps aside and Remembrance's reason is the one that declines.</summary>
        private static bool CheckRemembranceLineNotSaidToHiddenCompanion(StringBuilder sb)
        {
            bool ok = true;
            using (var rig = new StandDownRig("aibrain-remembrance-hidden"))
            {
                ok &= Check(sb, "remembrance rig (hidden): the live brain is built over the counting double", rig.WaitForLiveBrain());
                AiSettings settings = rig.Module.SettingsForDiagnostics;
                settings.StandDownForFullscreen = false;
                try
                {
                    rig.Host.RaiseFullscreenChanged(true);   // one monitor: the companion is hidden
                    rig.Publish(BusyJson("transcribing", rig.Now));
                    ClickTray(rig.Host, "Ask about my screen");
                    ok &= Check(sb, "remembrance: the refusal for a companion hidden behind a game is logged and not said, so no stale line plays after it",
                        rig.Started.Count == 0 && rig.CountLog("ask declined: remembrance stand-down (transcribing)") == 1 &&
                        rig.SaidTo(rig.Pet, AiBrainModule.RemembranceBusySpokenLine) == 0);
                    rig.Host.MovedToFreeMonitor.Add(rig.Pet);
                    ClickTray(rig.Host, "Ask about my screen");
                    ok &= Check(sb, "WITNESS remembrance: the same refusal for a companion in view beside the game is said once",
                        rig.SaidTo(rig.Pet, AiBrainModule.RemembranceBusySpokenLine) == 1);
                    rig.Publish("");
                    rig.Host.RaiseFullscreenChanged(false);
                }
                finally { settings.StandDownForFullscreen = true; }
            }
            return ok;
        }
    }
}
