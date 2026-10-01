using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using DesktopAICompanion.Ai;
using DesktopAICompanion.ModuleKit.Testing;   // RecordingHost, TempModuleStorage, FakeCompanion
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.AiBrainModule
{
    /// <summary>
    /// The module's HOST-FACING behaviour, driven through ModuleKit's <see cref="RecordingHost"/> the way the
    /// other modules' SelfTests drive theirs: what Init contributes, and above all what each entry point DECIDES
    /// before a turn reaches the engine. The engine legs in the sibling files exercise AiBrain and its backends
    /// with doubles; nothing exercised AiBrainModule itself under --module-selftest before 2026-09-29, which is
    /// how the routing argument at the heart of BUG-010 could be described in four places and asserted in none.
    ///
    /// No screen, no model, no network. The seeded settings turn the brain on with auto-start OFF, so ApplyState
    /// builds the brain without PrepareAsync, put the local slot on the OpenAI-compatible protocol, so retiring
    /// that brain at Shutdown is a no-op rather than a keep_alive:0 request to whatever Ollama this machine is
    /// running, and pin that slot to a loopback port nothing listens on (the shared seed, RA-072); and
    /// <see cref="AiBrainModule.AskSinkForDiagnostics"/> receives each started turn in place of AskCoreAsync.
    /// </summary>
    public static partial class AiEngineProbe
    {
        internal static bool RunModule(StringBuilder sb)
        {
            bool ok = true;
            // Each check under its own catch (RA-074): one that throws is a FAIL naming it, and the rest still run.
            ok &= GuardedCheck(sb, "CheckModuleEntryPoints", CheckModuleEntryPoints);
            ok &= GuardedCheck(sb, "CheckBrainStatusChannel", CheckBrainStatusChannel);
            return ok;
        }

        private static bool CheckModuleEntryPoints(StringBuilder sb)
        {
            bool ok = true;
            // Two process-globals the live module shares with this probe. Both go back exactly as found, because
            // --aibrain-selftest runs this probe from INSIDE a live module instance, which must keep logging and
            // keep its own settings root afterwards.
            Action<string> previousSink = AiBrain.LogSink;
            string previousRoot = AiPaths.CurrentRootForDiagnostics;
            AiBrainModule module = null;
            TempModuleStorage storage = null;
            try
            {
                storage = new TempModuleStorage("aibrain-entrypoints");
                // Written BEFORE Init, on purpose: Init's one-time migrator copies the user's real base
                // ai-settings.json into an EMPTY module store, and with the file already present it does nothing,
                // so this run can never inherit a real provider, key or consent. The SHARED seed, with vision on for
                // the routing checks below: this check used to write a seed of its own without the Endpoint pin, so
                // its module's local slot resolved to the machine's Ollama at localhost:11434 and the isolation
                // rested on the OpenAI-compat no-op unload and the AskSink seam alone (RA-072).
                SeedOfflineModuleSettings(storage, ", \"UseVision\": true");

                var host = new RecordingHost();
                host.UseStorage("aibrain", storage);
                module = new AiBrainModule();
                host.Declared = module.Info.Permissions;

                // (pet id, allowVision) for every turn an entry point STARTED. The bool is the whole point: it is
                // the argument BUG-010 was about.
                var started = new List<KeyValuePair<string, bool>>();
                module.AskSinkForDiagnostics = delegate(ScreenContext ctx, string zone, bool allowVision, ICompanion pet)
                {
                    started.Add(new KeyValuePair<string, bool>(pet == null ? "(none)" : pet.Id.ToString(), allowVision));
                    return Task.CompletedTask;
                };
                module.Init(host);

                ok &= Check(sb, "module: declares LaunchProcess, because it starts ollama and runs tesseract (F226)",
                    module.Info.Permissions.HasFlag(ModulePermissions.LaunchProcess));
                ok &= Check(sb, "module: Init registered one drop responder, one poke responder and the Ask hotkey",
                    host.CompanionDropResponders.Count == 1 && host.CompanionPokeResponders.Count == 1 &&
                    host.RegisteredHotkeys.Count == 1);
                ok &= Check(sb, "the module instance's local slot is pinned to a loopback port nothing listens on, as every instance in this probe is (RA-072)",
                    module.SettingsForDiagnostics != null &&
                    module.SettingsForDiagnostics.Endpoint == "http://127.0.0.1:9" &&
                    module.SettingsForDiagnostics.UseVision);
                ok &= Check(sb, "the pane's status row says the brain is on for a configuration CanUse accepts (R-013)",
                    host.OptionsPanes.Count == 1 && host.OptionsPanes[0].Load()["brainStatus"] == "On.");

                // ---- RA-060: an explicit-path refusal is said in the log; a responder's is not ----
                // No companion has been seen yet, so the tray row (the explicit path) has nobody to ask.
                ok &= Check(sb, "an explicit ask with no companion says so in the log (RA-060)",
                    ClickTray(host, "Ask about my screen") && started.Count == 0 &&
                    host.LoggedLines.Exists(delegate(string line)
                    {
                        return line.IndexOf("ask declined: no companion", StringComparison.Ordinal) >= 0;
                    }));

                var pet = new FakeCompanion(7, "eSheep");
                host.RaiseCompanionSpawned(pet);   // the tray row has no pet of its own; it asks about the last one seen

                host.SpeechEnabled = false;
                int declinedBefore = CountDeclined(host);
                ClickTray(host, "Ask about my screen");
                ok &= Check(sb, "an explicit ask with speech off says so in the log (RA-060)",
                    started.Count == 0 && CountDeclined(host) == declinedBefore + 1 &&
                    host.LoggedLines.Exists(delegate(string line)
                    {
                        return line.IndexOf("ask declined: speech off", StringComparison.Ordinal) >= 0;
                    }));
                bool dropWhileOff = host.RaiseDrop(pet);
                ok &= Check(sb, "WITNESS a responder's refusal is not logged: the declined drop falls through to Fortunes and the user hears one",
                    !dropWhileOff && started.Count == 0 && CountDeclined(host) == declinedBefore + 1);
                host.SpeechEnabled = true;

                // ---- BUG-010: the unprompted drop is a vision turn when vision is on (owner decision 2026-09-29) ----
                bool dropClaimed = host.RaiseDrop(pet);
                ok &= Check(sb, "the drop responder claims the tick with the brain on",
                    dropClaimed && started.Count == 1);
                ok &= Check(sb, "BUG-010: the unprompted drop asks WITH vision allowed (owner decision 2026-09-29)",
                    started.Count == 1 && started[0].Value);
                ok &= Check(sb, "the drop's turn belongs to the pet that was dropped",
                    started.Count == 1 && started[0].Key == "7");

                // The poke is the one text-only entry point, by design: a click needs a fast reaction.
                bool pokeClaimed = host.RaisePokeResponders(pet);
                ok &= Check(sb, "WITNESS the poke reaction asks WITHOUT vision, the one text-only entry point",
                    pokeClaimed && started.Count == 2 && !started[1].Value);

                // The tray row, which mirrors the hotkey (both are `Ask(null, true)`).
                ok &= Check(sb, "the 'Ask about my screen' tray row is offered while the brain is on",
                    ClickTray(host, "Ask about my screen"));
                ok &= Check(sb, "the tray ask allows vision and goes to the last pet seen",
                    started.Count == 3 && started[2].Value && started[2].Key == "7");

                // ---- F067: the stand-down covers the explicit path, which the drop and the poke already honoured ----
                host.IsFullscreenActive = true;
                int before = started.Count;
                ClickTray(host, "Ask about my screen");
                ok &= Check(sb, "the tray ask is DECLINED while a fullscreen app runs (F067)", started.Count == before);
                ok &= Check(sb, "...and the refusal is logged, since a refused hotkey has no fortune to fall through to",
                    host.LoggedLines.Exists(delegate(string line)
                    {
                        return line.IndexOf("ask declined: fullscreen stand-down", StringComparison.Ordinal) >= 0;
                    }));
                host.IsFullscreenActive = false;
                ClickTray(host, "Ask about my screen");
                ok &= Check(sb, "WITNESS with no fullscreen app the same tray click starts a turn", started.Count == before + 1);

                // The 30 s cooldown: a drop landing right after an ask yields a fortune, not two model answers.
                ok &= Check(sb, "a drop right after an ask is declined by the 30 s cooldown, so Fortunes answers instead",
                    !host.RaiseDrop(pet) && started.Count == before + 1);

                // Every started turn showed its pondering cue on the asked pet and nothing else was said: the seam
                // kept the turns away from any backend, which is the promise this whole probe rests on.
                int cues = 0;
                foreach (string said in host.SaidLines)
                    if (said != null && said.Length == 1 && said[0] == (char)0x2026) cues++;
                ok &= Check(sb, "each started turn showed the pondering cue and nothing reached a backend",
                    cues == started.Count && host.SaidLines.Count == started.Count);

                // ---- RA-058: a typed key that cannot be stored refuses the save and says why ----
                // Through the pane's own Save, with "(none)" for the provider: TrySetApiKey has no scope for the key.
                OptionsPane pane = host.OptionsPanes.Count == 1 ? host.OptionsPanes[0] : null;
                string liveNameBefore = module.SettingsForDiagnostics.CompanionName;
                var typedKeyNoProvider = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { "cloudProvider", "(none)" },
                    { "apiKey", "typed-with-no-provider-not-a-real-key" },
                    { "companionName", "Typed-Beside-A-Refused-Key" },
                };
                bool savedWithUnstorableKey = pane != null && pane.Save(typedKeyNoProvider);
                ok &= Check(sb, "a key typed with no provider selected is not stored, and the save says so instead of reporting success (RA-058)",
                    pane != null && !savedWithUnstorableKey &&
                    host.LoggedLines.Exists(delegate(string line)
                    {
                        return line.IndexOf("api key not stored:", StringComparison.Ordinal) >= 0;
                    }));
                // N-burn-aibrain-02: the refused save wrote NOTHING onto the live instance. Until 2026-10-01 the other
                // typed values landed on it before the key was judged, unsaved and unapplied until the next Apply.
                ok &= Check(sb, "a save refused for its key leaves the live settings as they were: the other typed values stay on screen, not on the instance (N-burn-aibrain-02)",
                    pane != null &&
                    string.Equals(module.SettingsForDiagnostics.CompanionName, liveNameBefore, StringComparison.Ordinal) &&
                    !string.Equals(liveNameBefore, "Typed-Beside-A-Refused-Key", StringComparison.Ordinal));
                ok &= Check(sb, "WITNESS the same save without a key succeeds",
                    pane != null && pane.Save(new Dictionary<string, string>(StringComparer.Ordinal) { { "cloudProvider", "(none)" } }));
                ok &= Check(sb, "WITNESS a save that goes ahead writes the typed values onto the live instance",
                    pane != null &&
                    pane.Save(new Dictionary<string, string>(StringComparer.Ordinal) { { "cloudProvider", "(none)" }, { "companionName", "Typed-And-Saved" } }) &&
                    string.Equals(module.SettingsForDiagnostics.CompanionName, "Typed-And-Saved", StringComparison.Ordinal));
            }
            catch (Exception ex)
            {
                ok &= Check(sb, "module entry-point probe threw " + ex.GetType().Name + ": " + ex.Message, false);
            }
            finally
            {
                try { if (module != null) module.Shutdown(); } catch { }
                AiBrain.LogSink = previousSink;
                AiPaths.SwapRoot(previousRoot);
                if (storage != null) storage.Dispose();
            }
            return ok;
        }

        /// <summary>R-013. CanUse's refusal reaches the user: the pane's Status row names it and the inert tray row is
        /// hidden while the brain is not started; the log line ApplyState writes stays.</summary>
        private static bool CheckBrainStatusChannel(StringBuilder sb)
        {
            bool ok = true;
            Action<string> previousSink = AiBrain.LogSink;
            string previousRoot = AiPaths.CurrentRootForDiagnostics;
            AiBrainModule module = null;
            TempModuleStorage storage = null;
            try
            {
                storage = new TempModuleStorage("aibrain-brain-status");
                // A cloud slot with consent and no cloud text model: the F101 shape CanUse refuses. The local slot
                // keeps the loopback pin the seed applies, although nothing here reaches it.
                SeedOfflineModuleSettings(storage,
                    ", \"Provider\": \"openai\", \"OpenAiBaseUrl\": \"https://api.openai.com/v1\", " +
                    "\"CloudDataConsent\": true, \"CloudTextModel\": \"\"");
                var host = new RecordingHost();
                host.UseStorage("aibrain", storage);
                module = new AiBrainModule();
                host.Declared = module.Info.Permissions;
                module.Init(host);

                IReadOnlyDictionary<string, string> shown = host.OptionsPanes.Count == 1 ? host.OptionsPanes[0].Load() : null;
                string status = shown != null && shown.ContainsKey("brainStatus") ? shown["brainStatus"] : "";
                ok &= Check(sb, "the pane's status names the reason the brain is not started (R-013)",
                    status.StartsWith("Not started:", StringComparison.Ordinal) &&
                    status.IndexOf("cloud text model", StringComparison.OrdinalIgnoreCase) >= 0);
                ok &= Check(sb, "the tray row is hidden while the brain is not started, instead of offered and inert (R-013)",
                    !ClickTray(host, "Ask about my screen"));
                ok &= Check(sb, "WITNESS the toggle row still reads the stored switch and offers 'Disable AI'",
                    TrayText(host, "Enable AI") == "Disable AI");
                ok &= Check(sb, "WITNESS the log still carries ApplyState's line",
                    host.LoggedLines.Exists(delegate(string line)
                    {
                        return line.IndexOf("AI brain not started:", StringComparison.Ordinal) >= 0;
                    }));
                ok &= Check(sb, "the status says 'Off' for a disabled brain and names the switch",
                    AiBrainModule.BrainStatusLine(new AiSettings { AiBrainEnabled = false }).StartsWith("Off", StringComparison.Ordinal) &&
                    AiBrainModule.BrainStatusLine(new AiSettings { AiBrainEnabled = false }).IndexOf("Enable AI brain", StringComparison.Ordinal) >= 0);
            }
            catch (Exception ex)
            {
                ok &= Check(sb, "brain status probe threw " + ex.GetType().Name + ": " + ex.Message, false);
            }
            finally
            {
                try { if (module != null) module.Shutdown(); } catch { }
                AiBrain.LogSink = previousSink;
                AiPaths.SwapRoot(previousRoot);
                if (storage != null) storage.Dispose();
            }
            return ok;
        }

        /// <summary>Press the tray row whose label contains <paramref name="fragment"/>, honouring its Visible
        /// predicate as the host's tray does. False when no such row is offered.</summary>
        private static bool ClickTray(RecordingHost host, string fragment)
        {
            foreach (TrayItem item in host.TrayItems)
            {
                if (item == null || item.Label == null || item.Click == null) continue;
                if (item.Label.IndexOf(fragment, StringComparison.Ordinal) < 0) continue;
                if (item.Visible != null && !item.Visible()) return false;
                item.Click();
                return true;
            }
            return false;
        }

        /// <summary>The text the tray shows for the row whose label contains <paramref name="fragment"/>: its
        /// DynamicText when it has one, else the label. Null when there is no such row.</summary>
        private static string TrayText(RecordingHost host, string fragment)
        {
            foreach (TrayItem item in host.TrayItems)
            {
                if (item == null || item.Label == null || item.Label.IndexOf(fragment, StringComparison.Ordinal) < 0) continue;
                return item.DynamicText != null ? item.DynamicText() : item.Label;
            }
            return null;
        }

        /// <summary>How many "ask declined:" lines the host has logged (RA-060).</summary>
        private static int CountDeclined(RecordingHost host)
        {
            int count = 0;
            foreach (string line in host.LoggedLines)
                if (line != null && line.IndexOf("ask declined:", StringComparison.Ordinal) >= 0) count++;
            return count;
        }
    }
}
