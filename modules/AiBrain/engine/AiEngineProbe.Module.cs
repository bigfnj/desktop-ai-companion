using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
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
            // Lane feature/aibrain-standdown (1.2.0): the stand-down's second reason, Remembrance's busy flag, driven
            // through the published context on this same RecordingHost, never through Remembrance itself.
            ok &= GuardedCheck(sb, "CheckRemembranceFlagReading", CheckRemembranceFlagReading);
            ok &= GuardedCheck(sb, "CheckRemembranceStandDown", CheckRemembranceStandDown);
            ok &= GuardedCheck(sb, "CheckRemembranceReachesNoBackend", CheckRemembranceReachesNoBackend);
            ok &= GuardedCheck(sb, "CheckRemembranceAndFullscreenOverlap", CheckRemembranceAndFullscreenOverlap);
            ok &= GuardedCheck(sb, "CheckRemembranceSwitch", CheckRemembranceSwitch);
            ok &= GuardedCheck(sb, "CheckRemembranceStaleAndMalformed", CheckRemembranceStaleAndMalformed);
            ok &= GuardedCheck(sb, "CheckRemembranceFlagBeforeInit", CheckRemembranceFlagBeforeInit);
            ok &= GuardedCheck(sb, "CheckRemembranceApplyLeavesModelsAlone", CheckRemembranceApplyLeavesModelsAlone);
            ok &= GuardedCheck(sb, "CheckRemembrancePaneActions", CheckRemembrancePaneActions);
            ok &= GuardedCheck(sb, "CheckRemembranceCloudSlot", CheckRemembranceCloudSlot);
            // Lane feature/fullscreen-per-monitor (1.4.0): the stand-down's two questions, in AiEngineProbe.Fullscreen.cs.
            ok &= GuardedCheck(sb, "CheckFullscreenOnCli", CheckFullscreenOnCli);
            ok &= GuardedCheck(sb, "CheckFullscreenOnLocal", CheckFullscreenOnLocal);
            ok &= GuardedCheck(sb, "CheckFullscreenCloudFallback", CheckFullscreenCloudFallback);
            ok &= GuardedCheck(sb, "CheckRemembranceLineNotSaidToHiddenCompanion", CheckRemembranceLineNotSaidToHiddenCompanion);
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
                    // The Status card's line begins with BrainStatusLine's answer (aibrain 1.3.0, feature/cli-backend).
                    host.OptionsPanes.Count == 1 && host.OptionsPanes[0].Load()["brainStatus"].StartsWith("On.  |  ", StringComparison.Ordinal));

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

        // ---- lane feature/aibrain-standdown: Remembrance's busy flag (1.2.0) ------------------------------------------
        //
        // Every module instance below is a StandDownRig: the shared offline seed (RA-072), a StandDownProbeBackend behind
        // the live brain (BrainFactoryForDiagnostics) unless a check needs the shipped CreateBrain, the AskSink recording
        // each started turn unless a check needs the real path, and a clock the check moves (UtcNowForDiagnostics). The
        // flag is published on the RecordingHost as Remembrance publishes it and read back by the module at use, which
        // is the only way it reads it (Addendum 1 of the lane's brief).

        /// <summary>The value Remembrance publishes while busy, in the contract's round-trip form ("at", Addendum 1).</summary>
        private static string BusyJson(string phase, DateTime atUtc)
        {
            return "{\"phase\":\"" + phase + "\",\"at\":\"" + atUtc.ToString("o", CultureInfo.InvariantCulture) + "\"}";
        }

        /// <summary>The contract's parsing, pure: what is busy, what is clear, what is malformed, and freshness.</summary>
        private static bool CheckRemembranceFlagReading(StringBuilder sb)
        {
            bool ok = true;
            string malformed;
            DateTime now = new DateTime(2026, 10, 2, 10, 15, 30, DateTimeKind.Utc);

            RemembranceBusyFlag busy = RemembranceBusyFlag.Parse(BusyJson("transcribing", now), out malformed);
            ok &= Check(sb, "flag: the contract's busy value is read as busy, with its phase and its at",
                busy != null && malformed == null && busy.Phase == "transcribing" && busy.AtUtc == now);
            foreach (string phase in new[] { "summarizing", "validating" })
            {
                RemembranceBusyFlag other = RemembranceBusyFlag.Parse(BusyJson(phase, now), out malformed);
                ok &= Check(sb, "flag: a phase the contract names is read as busy: " + phase,
                    other != null && malformed == null && other.Phase == phase);
            }
            ok &= Check(sb, "flag: the cleared value \"\" is clear, and not malformed",
                RemembranceBusyFlag.Parse("", out malformed) == null && malformed == null);
            ok &= Check(sb, "flag: an absent key (ReadContext answers \"\"; null and blanks tolerated) is clear, and not malformed",
                RemembranceBusyFlag.Parse(null, out malformed) == null && malformed == null &&
                RemembranceBusyFlag.Parse("   ", out malformed) == null && malformed == null);

            // Malformed shapes: each fails open and names why. { value, the reason's words, what the shape is }.
            string at = "\"at\":\"2026-10-02T10:15:30.0000000Z\"";
            string[][] bad =
            {
                new[] { "busy", "not JSON", "a bare word" },
                new[] { "[\"transcribing\"]", "not a JSON object", "an array" },
                new[] { "{" + at + "}", "names no phase", "no phase key" },
                new[] { "{\"phase\":42," + at + "}", "names no phase", "a numeric phase" },
                new[] { "{\"phase\":\"diarizing\"," + at + "}", "not one the contract names", "a phase the contract does not name" },
                new[] { "{\"phase\":\"Transcribing\"," + at + "}", "not one the contract names", "a phase in the wrong case" },
                new[] { "{\"phase\":\"transcribing\"}", "not an ISO-8601 UTC time", "no at key" },
                new[] { "{\"phase\":\"transcribing\",\"since\":\"2026-10-02T10:15:30.0000000Z\"}", "not an ISO-8601 UTC time", "the superseded since in place of at" },
                new[] { "{\"phase\":\"transcribing\",\"at\":\"yesterday\"}", "not an ISO-8601 UTC time", "an at in words" },
                new[] { "{\"phase\":\"transcribing\",\"at\":\"2026-10-02T10:15:30\"}", "not an ISO-8601 UTC time", "an at with no zone" },
                new[] { "{\"phase\":\"transcribing\",\"at\":\"" + new string('9', RemembranceBusyFlag.MaximumCharacters) + "\"}", "longer than", "an over-long value" },
            };
            foreach (string[] shape in bad)
            {
                RemembranceBusyFlag read = RemembranceBusyFlag.Parse(shape[0], out malformed);
                ok &= Check(sb, "flag: a malformed value fails open (not busy) and says why: " + shape[2],
                    read == null && malformed != null && malformed.IndexOf(shape[1], StringComparison.Ordinal) >= 0);
            }

            // "at" in the ISO-8601 forms a publisher may write: "o" with Z, "o" with an offset, System.Text.Json's trimmed
            // fraction, none at all; an offset is converted, not dropped.
            string[][] atForms =
            {
                new[] { "2026-10-02T10:15:30.0000000Z", "Z and seven fraction digits" },
                new[] { "2026-10-02T10:15:30.0000000+00:00", "an explicit +00:00" },
                new[] { "2026-10-02T10:15:30Z", "no fraction" },
                new[] { "2026-10-02T12:15:30+02:00", "another offset, converted to UTC" },
            };
            foreach (string[] form in atForms)
            {
                RemembranceBusyFlag read = RemembranceBusyFlag.Parse(
                    "{\"phase\":\"transcribing\",\"at\":\"" + form[0] + "\"}", out malformed);
                ok &= Check(sb, "flag: at is read in the ISO-8601 form with " + form[1],
                    read != null && read.AtUtc == now && read.AtUtc.Kind == DateTimeKind.Utc);
            }
            RemembranceBusyFlag trimmed = RemembranceBusyFlag.Parse(
                "{\"phase\":\"transcribing\",\"at\":\"2026-10-02T10:15:30.12Z\"}", out malformed);
            ok &= Check(sb, "flag: at is read with a trimmed fraction",
                trimmed != null && trimmed.AtUtc == now.AddMilliseconds(120));

            // Freshness: eight hours either side of the clock.
            ok &= Check(sb, "flag: an at 7 h 59 m old is fresh",
                busy != null && busy.IsFreshAt(now + TimeSpan.FromHours(8) - TimeSpan.FromMinutes(1)));
            ok &= Check(sb, "flag: an at 8 h 1 m old is stale",
                busy != null && !busy.IsFreshAt(now + TimeSpan.FromHours(8) + TimeSpan.FromMinutes(1)));
            ok &= Check(sb, "flag: an at 8 h 1 m ahead of the clock is stale too",
                busy != null && !busy.IsFreshAt(now - TimeSpan.FromHours(8) - TimeSpan.FromMinutes(1)));
            ok &= Check(sb, "WITNESS flag: an at an hour ahead of the clock is still fresh",
                busy != null && busy.IsFreshAt(now - TimeSpan.FromHours(1)));
            ok &= Check(sb, "flag: standing down for Remembrance is on by default", new AiSettings().StandDownForRemembrance);
            return ok;
        }

        /// <summary>The flag set, on the local slot: every entry point declines, the explicit Ask with a logged reason
        /// of its own, the Status row names the phase, nothing is released, and nothing ever subscribed. Cleared: AI
        /// Brain resumes.</summary>
        private static bool CheckRemembranceStandDown(StringBuilder sb)
        {
            bool ok = true;
            using (var rig = new StandDownRig("aibrain-remembrance-standdown"))
            {
                ok &= Check(sb, "remembrance rig: the live brain is built over the counting double", rig.WaitForLiveBrain());
                ok &= Check(sb, "remembrance: the module never subscribes to ContextChanged; it reads the flag at use",
                    ContextSubscribers(rig.Host) == 0);
                Action<string> probe = delegate(string key) { };
                rig.Host.ContextChanged += probe;
                ok &= Check(sb, "WITNESS remembrance: the subscriber count sees a handler, so the zero above is not blindness",
                    ContextSubscribers(rig.Host) == 1);
                rig.Host.ContextChanged -= probe;
                int unloads = rig.Backend.UnloadCalls;
                int declinedBefore = CountDeclined(rig.Host);

                rig.Publish(BusyJson("transcribing", rig.Now));
                ok &= Check(sb, "remembrance: the drop is declined while Remembrance is transcribing, so Fortunes answers",
                    !rig.Host.RaiseDrop(rig.Pet) && rig.Started.Count == 0);
                ok &= Check(sb, "remembrance: the poke is declined while Remembrance is transcribing",
                    !rig.Host.RaisePokeResponders(rig.Pet) && rig.Started.Count == 0);
                ok &= Check(sb, "remembrance: the declined drop and poke log nothing, as no responder refusal does (RA-060)",
                    CountDeclined(rig.Host) == declinedBefore);
                // The unprompted paths are SILENT: they fall through to Fortunes, which is what speaks there. The
                // positive control for the spoken explicit refusal below, and the check that sees a responder that
                // lost its own Remembrance check and reached Ask's, which speaks.
                ok &= Check(sb, "WITNESS remembrance: an unprompted drop or poke during the span says nothing; it falls through to Fortunes",
                    rig.Host.SaidLines.Count == 0);
                bool offered = ClickTray(rig.Host, "Ask about my screen");
                ok &= Check(sb, "remembrance: the tray ask is DECLINED while Remembrance is transcribing",
                    offered && rig.Started.Count == 0);
                ok &= Check(sb, "remembrance: the declined ask is logged under its own category, naming the phase",
                    rig.CountLog("ask declined: remembrance stand-down (transcribing)") == 1 &&
                    CountDeclined(rig.Host) == declinedBefore + 1);
                ok &= Check(sb, "remembrance: the declined explicit ask SAYS that Remembrance is using the model, once, to the companion it was for",
                    rig.SaidTo(rig.Pet, AiBrainModule.RemembranceBusySpokenLine) == 1 && rig.Host.SaidLines.Count == 1);
                ok &= Check(sb, "remembrance: the Status row names the reason",
                    rig.Status() == "Standing down while Remembrance is transcribing.");
                rig.Publish(BusyJson("summarizing", rig.Now));
                ok &= Check(sb, "remembrance: the Status row follows the phase",
                    rig.Status() == "Standing down while Remembrance is summarizing.");
                ok &= Check(sb, "remembrance: nothing is released or evicted while Remembrance is busy, since AI Brain's model can be the one it uses",
                    rig.Backend.UnloadCalls == unloads);

                rig.Publish("");
                ok &= Check(sb, "WITNESS remembrance: with the flag cleared the Status row reads On again", rig.Status() == "On.");
                // The drop first: a turn started by any entry point arms the drop's 30 s cooldown.
                ok &= Check(sb, "WITNESS remembrance: with the flag cleared the drop starts a turn again",
                    rig.Host.RaiseDrop(rig.Pet) && rig.Started.Count == 1);
                ok &= Check(sb, "WITNESS remembrance: with the flag cleared the poke starts a turn again",
                    rig.Host.RaisePokeResponders(rig.Pet) && rig.Started.Count == 2);
                ok &= Check(sb, "WITNESS remembrance: with the flag cleared the tray ask starts a turn again",
                    ClickTray(rig.Host, "Ask about my screen") && rig.Started.Count == 3);
            }
            return ok;
        }

        /// <summary>The flag set: the ask reaches no backend at all. WITNESS: cleared, the same press reaches the double.
        /// No AskSink here, so a started turn runs the real path into the session and the brain.</summary>
        private static bool CheckRemembranceReachesNoBackend(StringBuilder sb)
        {
            bool ok = true;
            using (var rig = new StandDownRig("aibrain-remembrance-backend", sinkTurns: false))
            {
                ok &= Check(sb, "remembrance rig (no sink): the live brain is built over the counting double", rig.WaitForLiveBrain());
                rig.Publish(BusyJson("transcribing", rig.Now));
                rig.Host.RaiseDrop(rig.Pet);
                ok &= Check(sb, "WITNESS remembrance: on the real path an unprompted drop during the span stays silent",
                    rig.Host.SaidLines.Count == 0);
                ClickTray(rig.Host, "Ask about my screen");
                rig.Host.RaisePokeResponders(rig.Pet);
                // A refused turn starts nothing, so nothing can arrive late; the wait is for a mutant that starts one.
                SpinWait.SpinUntil(delegate { return rig.Backend.Requests > 0; }, TimeSpan.FromMilliseconds(300));
                ok &= Check(sb, "remembrance: while Remembrance is busy the ask and the poke reach no backend: no probe, no chat",
                    rig.Backend.Requests == 0);
                ok &= Check(sb, "remembrance: on the real path the explicit press speaks its one line and that is all it does",
                    rig.SaidTo(rig.Pet, AiBrainModule.RemembranceBusySpokenLine) == 1 && rig.Host.SaidLines.Count == 1 &&
                    rig.Backend.Requests == 0);
                rig.Publish("");
                ClickTray(rig.Host, "Ask about my screen");
                ok &= Check(sb, "WITNESS remembrance: the same tray ask with the flag cleared reaches the fake backend",
                    SpinWait.SpinUntil(delegate { return rig.Backend.AvailabilityCalls > 0; }, TimeSpan.FromSeconds(5)));
            }
            return ok;
        }

        /// <summary>A fullscreen app and Remembrance together: AI Brain resumes only when both have ended, in either
        /// order. And the fullscreen release is withheld while Remembrance is busy, and only then.</summary>
        private static bool CheckRemembranceAndFullscreenOverlap(StringBuilder sb)
        {
            bool ok = true;
            using (var rig = new StandDownRig("aibrain-remembrance-overlap"))
            {
                ok &= Check(sb, "remembrance rig (overlap): the live brain is built over the counting double", rig.WaitForLiveBrain());

                // Fullscreen first, Remembrance second; Remembrance clears first.
                rig.Host.RaiseFullscreenChanged(true);
                rig.Publish(BusyJson("transcribing", rig.Now));
                ok &= Check(sb, "overlap: a fullscreen app and Remembrance together decline the poke",
                    !rig.Host.RaisePokeResponders(rig.Pet) && rig.Started.Count == 0);
                ClickTray(rig.Host, "Ask about my screen");
                // NOT said: a stood-down companion defers a line and replays it when the monitor clears (F067, F278).
                ok &= Check(sb, "overlap: an ask declined for both reasons is logged once, as the fullscreen stand-down checked first, and is not said",
                    rig.Started.Count == 0 && rig.CountLog("ask declined: fullscreen stand-down") == 1 &&
                    rig.CountLog("ask declined: remembrance stand-down") == 0 && rig.Host.SaidLines.Count == 0);
                // From here the companion stands IN VIEW on a free monitor (aibrain 1.4.0): a hidden one is refused by the
                // per-companion check whatever the guard does, which would hide a guard that lapsed with Remembrance.
                rig.Host.MovedToFreeMonitor.Add(rig.Pet);
                rig.Publish("");
                ok &= Check(sb, "overlap: Remembrance clearing while the fullscreen app runs keeps the stand-down",
                    !rig.Host.RaisePokeResponders(rig.Pet) && rig.Started.Count == 0);
                rig.Host.RaiseFullscreenChanged(false);
                ok &= Check(sb, "WITNESS overlap: once both reasons have ended the poke starts a turn",
                    rig.Host.RaisePokeResponders(rig.Pet) && rig.Started.Count == 1);

                // Remembrance first, fullscreen second; the fullscreen app closes first.
                rig.Publish(BusyJson("summarizing", rig.Now));
                rig.Host.RaiseFullscreenChanged(true);
                rig.Host.RaiseFullscreenChanged(false);
                ok &= Check(sb, "overlap: the fullscreen app closing while Remembrance is busy keeps the stand-down",
                    !rig.Host.RaisePokeResponders(rig.Pet) && rig.Started.Count == 1);
                rig.Publish("");
                ok &= Check(sb, "WITNESS overlap: once Remembrance clears as well the poke starts a turn",
                    rig.Host.RaisePokeResponders(rig.Pet) && rig.Started.Count == 2);

                // The fullscreen release, withheld while Remembrance is busy and only then.
                rig.Publish(BusyJson("transcribing", rig.Now));
                int beforeGame = rig.Backend.UnloadCalls;
                rig.Host.RaiseFullscreenChanged(true);
                rig.Host.RaisePokeResponders(rig.Pet);
                ok &= Check(sb, "overlap: a game starting while Remembrance is busy releases nothing, nor does the check after it",
                    rig.Backend.UnloadCalls == beforeGame);
                rig.Host.RaiseFullscreenChanged(false);
                rig.Publish("");
                int alone = rig.Backend.UnloadCalls;
                rig.Host.RaiseFullscreenChanged(true);
                ok &= Check(sb, "WITNESS fullscreen: a game starting with Remembrance idle still releases the model",
                    rig.Backend.UnloadCalls == alone + 1);
                rig.Host.RaisePokeResponders(rig.Pet);
                ok &= Check(sb, "WITNESS fullscreen: a poke declined for a game alone still releases, the case no transition covers",
                    rig.Backend.UnloadCalls == alone + 2);
                rig.Host.RaiseFullscreenChanged(false);
            }
            return ok;
        }

        /// <summary>The switch: off ignores the flag, on honours it, and the pane round-trips it.</summary>
        private static bool CheckRemembranceSwitch(StringBuilder sb)
        {
            bool ok = true;
            using (var rig = new StandDownRig("aibrain-remembrance-switch"))
            {
                ok &= Check(sb, "remembrance rig (switch): the live brain is built over the counting double", rig.WaitForLiveBrain());
                ok &= Check(sb, "remembrance switch: on for a settings file that has never seen it, and the pane says so",
                    rig.Module.SettingsForDiagnostics.StandDownForRemembrance &&
                    rig.Pane.Load()["standDownRemembrance"] == "true");
                rig.Publish(BusyJson("transcribing", rig.Now));
                bool saved = rig.Pane.Save(new Dictionary<string, string>(StringComparer.Ordinal) { { "standDownRemembrance", "false" } });
                ok &= Check(sb, "remembrance switch: turned off from the pane it is stored, and the pane reads it back",
                    saved && !rig.Module.SettingsForDiagnostics.StandDownForRemembrance &&
                    rig.Pane.Load()["standDownRemembrance"] == "false");
                ok &= Check(sb, "remembrance switch: OFF ignores the flag: the poke starts a turn while Remembrance is busy",
                    rig.Host.RaisePokeResponders(rig.Pet) && rig.Started.Count == 1);
                bool resaved = rig.Pane.Save(new Dictionary<string, string>(StringComparer.Ordinal) { { "standDownRemembrance", "true" } });
                ok &= Check(sb, "WITNESS remembrance switch: ON honours the flag: the poke is declined again",
                    resaved && !rig.Host.RaisePokeResponders(rig.Pet) && rig.Started.Count == 1);
            }
            return ok;
        }

        /// <summary>A stale flag is ignored and logged once; one never republished stops counting after 8 hours; a
        /// malformed one fails open and is logged once however often it is read.</summary>
        private static bool CheckRemembranceStaleAndMalformed(StringBuilder sb)
        {
            bool ok = true;
            using (var rig = new StandDownRig("aibrain-remembrance-stale"))
            {
                ok &= Check(sb, "remembrance rig (stale): the live brain is built over the counting double", rig.WaitForLiveBrain());

                rig.Publish(BusyJson("transcribing", rig.Now - TimeSpan.FromHours(9)));
                ok &= Check(sb, "remembrance: a flag whose at is more than 8 hours old is ignored: the poke starts a turn",
                    rig.Host.RaisePokeResponders(rig.Pet) && rig.Started.Count == 1);
                rig.Host.RaisePokeResponders(rig.Pet);
                rig.Status();
                ok &= Check(sb, "remembrance: a stale value is logged once, however often it is read",
                    rig.CountLog("more than 8 hours ago") == 1 && rig.Started.Count == 2);

                rig.Publish(BusyJson("summarizing", rig.Now));
                ok &= Check(sb, "WITNESS remembrance: a fresh flag is honoured: the poke is declined",
                    !rig.Host.RaisePokeResponders(rig.Pet) && rig.Started.Count == 2);
                rig.Now = rig.Now + TimeSpan.FromHours(8) + TimeSpan.FromMinutes(1);
                ok &= Check(sb, "remembrance: a flag never republished stops counting 8 hours after its at: the poke starts a turn",
                    rig.Host.RaisePokeResponders(rig.Pet) && rig.Started.Count == 3);

                rig.Publish(BusyJson("transcribing", rig.Now + TimeSpan.FromHours(9)));
                ok &= Check(sb, "remembrance: an at more than 8 hours AHEAD of the clock is ignored too, and logged",
                    rig.Host.RaisePokeResponders(rig.Pet) && rig.Started.Count == 4 &&
                    rig.CountLog("hours ahead of this clock") == 1);

                rig.Publish("not json at all");
                rig.Host.RaisePokeResponders(rig.Pet);
                rig.Host.RaisePokeResponders(rig.Pet);
                rig.Status();
                ok &= Check(sb, "remembrance: a malformed flag fails open: the pokes start turns", rig.Started.Count == 6);
                ok &= Check(sb, "remembrance: a malformed value is logged once, however often it is read",
                    rig.CountLog("remembrance.busy ignored: the value is not JSON") == 1);
                rig.Publish("{\"phase\":\"diarizing\",\"at\":\"" + rig.Now.ToString("o", CultureInfo.InvariantCulture) + "\"}");
                rig.Host.RaisePokeResponders(rig.Pet);
                ok &= Check(sb, "WITNESS remembrance: a different malformed value is logged too, naming its word",
                    rig.CountLog("its phase 'diarizing' is not one the contract names") == 1 && rig.Started.Count == 7);

                rig.Publish(BusyJson("transcribing", rig.Now));
                ok &= Check(sb, "WITNESS remembrance: a well-formed flag after the malformed ones is honoured",
                    !rig.Host.RaisePokeResponders(rig.Pet) && rig.Started.Count == 7);
                rig.Publish("{\"phase\":\"transcribing\",\"since\":\"" + rig.Now.ToString("o", CultureInfo.InvariantCulture) + "\"}");
                ok &= Check(sb, "remembrance: a malformed value ends a stand-down in progress: the superseded since shape fails open",
                    rig.Host.RaisePokeResponders(rig.Pet) && rig.Started.Count == 8);
            }
            return ok;
        }

        /// <summary>A flag published before AI Brain loaded is honoured: the read at use finds what the host retained.</summary>
        private static bool CheckRemembranceFlagBeforeInit(StringBuilder sb)
        {
            bool ok = true;
            using (var rig = new StandDownRig("aibrain-remembrance-before-init",
                publishBeforeInit: BusyJson("transcribing", DateTime.UtcNow)))
            {
                ok &= Check(sb, "remembrance rig (before Init): the live brain is built over the counting double", rig.WaitForLiveBrain());
                ok &= Check(sb, "remembrance: a flag published BEFORE Init is honoured at the first decision: the Status row names it",
                    rig.Status() == "Standing down while Remembrance is transcribing.");
                ok &= Check(sb, "remembrance: a flag published before Init declines the poke",
                    !rig.Host.RaisePokeResponders(rig.Pet) && rig.Started.Count == 0);
            }
            using (var rig = new StandDownRig("aibrain-remembrance-before-init-witness"))
            {
                ok &= Check(sb, "WITNESS remembrance: with nothing published before Init the Status row reads On",
                    rig.WaitForLiveBrain() && rig.Status() == "On.");
            }
            return ok;
        }

        /// <summary>ApplyState while Remembrance is busy: the retiring brain's model is not evicted (an Apply, and AI
        /// switched off), and the new brain's preparation warms nothing; with the flag cleared both happen again.</summary>
        private static bool CheckRemembranceApplyLeavesModelsAlone(StringBuilder sb)
        {
            bool ok = true;
            // "unload", the seed's residency: every retirement evicts, except while Remembrance is busy.
            using (var rig = new StandDownRig("aibrain-remembrance-apply-unload"))
            {
                ok &= Check(sb, "remembrance rig (apply): the live brain is built over the counting double", rig.WaitForLiveBrain());
                rig.Publish(BusyJson("transcribing", rig.Now));
                int unloads = rig.Backend.UnloadCalls;
                bool rebuilt = rig.ApplyAndWaitForNewBrain();
                ok &= Check(sb, "remembrance: an Apply while Remembrance is busy retires the old brain without evicting its model",
                    rebuilt && rig.Backend.UnloadCalls == unloads);
                bool switchedOff = ClickTray(rig.Host, "Enable AI") && rig.WaitForNoBrain();
                ok &= Check(sb, "remembrance: switching AI off while Remembrance is busy evicts nothing either",
                    switchedOff && rig.Backend.UnloadCalls == unloads);
                ClickTray(rig.Host, "Enable AI");
                rig.Publish("");
                bool back = rig.WaitForLiveBrain();
                int clearUnloads = rig.Backend.UnloadCalls;
                bool rebuiltClear = rig.ApplyAndWaitForNewBrain();
                ok &= Check(sb, "WITNESS remembrance: the same Apply with the flag cleared evicts the retiring brain's model",
                    back && rebuiltClear && rig.Backend.UnloadCalls == clearUnloads + 1);
            }
            // "keep": the launch preparation warms the model, except while Remembrance is busy.
            using (var rig = new StandDownRig("aibrain-remembrance-apply-keep", extraSeed: ", \"ModelResidency\": \"keep\""))
            {
                ok &= Check(sb, "remembrance rig (keep): the live brain is built over the counting double", rig.WaitForLiveBrain());
                // Init's own preparation probes once (the double answers "down", so it warms nothing); let it finish
                // before the double starts answering "up".
                SpinWait.SpinUntil(delegate { return rig.Backend.AvailabilityCalls > 0; }, TimeSpan.FromSeconds(5));
                rig.Backend.Available = true;
                rig.Publish(BusyJson("transcribing", rig.Now));
                int probes = rig.Backend.AvailabilityCalls;
                int warmUps = rig.Backend.WarmUpCalls;
                rig.Apply();
                bool prepared = SpinWait.SpinUntil(delegate { return rig.Backend.AvailabilityCalls > probes; }, TimeSpan.FromSeconds(5));
                Thread.Sleep(50);   // a warm-up follows the probe at once on this double; give a mutant's the time
                ok &= Check(sb, "remembrance: an Apply while Remembrance is busy still prepares the backend, and warms no model",
                    prepared && rig.Backend.WarmUpCalls == warmUps);
                rig.Publish("");
                rig.Apply();
                ok &= Check(sb, "WITNESS remembrance: the same Apply with the flag cleared warms the model under keep",
                    SpinWait.SpinUntil(delegate { return rig.Backend.WarmUpCalls > warmUps; }, TimeSpan.FromSeconds(5)));
            }
            return ok;
        }

        /// <summary>The pane actions that send a chat to the LOCAL model answer that Remembrance is using it and send
        /// nothing; with the flag cleared they run.</summary>
        private static bool CheckRemembrancePaneActions(StringBuilder sb)
        {
            bool ok = true;
            using (var rig = new StandDownRig("aibrain-remembrance-pane"))
            {
                ok &= Check(sb, "remembrance rig (pane): the live brain is built over the counting double", rig.WaitForLiveBrain());
                int auditions = 0;
                rig.Module.AuditionBrainFactoryForDiagnostics = delegate(AiSettings s, int? keepAlive)
                {
                    auditions++;
                    return new AiBrain(new RecordingBackend("{\"text\":\"REMARK\",\"emotion\":\"neutral\"}", true), s.ActiveSlotSnapshot());
                };
                // The Local provider card's Test connection, the local slot's own since lane feature/layout-aibrain (the
                // Cloud provider card has one of the same label, for the cloud slot).
                PaneAction test = FindAction(rig.Pane, "Local provider", "Test connection");
                // An endpoint the policy refuses, so a test that DOES run answers at once, with no network.
                var refusedLocal = new Dictionary<string, string>(StringComparer.Ordinal) { { "endpoint", "not a url" } };

                rig.Publish(BusyJson("transcribing", rig.Now));
                ok &= Check(sb, "remembrance: Show me 5 examples on the local slot sends nothing while Remembrance is busy",
                    rig.Module.PreviewDispositionAsync(false).GetAwaiter().GetResult() == AiBrainModule.RemembranceBusyAnswer &&
                    auditions == 0);
                ok &= Check(sb, "remembrance: 5 about my screen on the local slot sends nothing while Remembrance is busy",
                    rig.Module.PreviewDispositionAsync(true).GetAwaiter().GetResult() == AiBrainModule.RemembranceBusyAnswer &&
                    auditions == 0);
                ok &= Check(sb, "remembrance: Test connection on the local slot sends nothing while Remembrance is busy",
                    test != null && test.InvokeWithPendingAsync(refusedLocal).GetAwaiter().GetResult() == AiBrainModule.RemembranceBusyAnswer);

                rig.Publish("");
                string audition = rig.Module.PreviewDispositionAsync(false).GetAwaiter().GetResult();
                ok &= Check(sb, "WITNESS remembrance: with the flag cleared Show me 5 examples runs",
                    auditions == 1 && audition != null && audition.IndexOf("as currently saved", StringComparison.Ordinal) >= 0);
                string tested = test != null ? test.InvokeWithPendingAsync(refusedLocal).GetAwaiter().GetResult() : null;
                ok &= Check(sb, "WITNESS remembrance: with the flag cleared Test connection answers for itself (here, refusing the endpoint it was given)",
                    tested != null && tested.StartsWith("✗", StringComparison.Ordinal));
            }
            return ok;
        }

        /// <summary>A cloud slot: its requests go ahead while Remembrance is busy, and only its fallback to the local slot
        /// is held back. Built through the shipped CreateBrain (no live-brain seam), so the composite and the module's
        /// hold are the real ones; construction touches no network and the AskSink keeps every turn off the wire.</summary>
        private static bool CheckRemembranceCloudSlot(StringBuilder sb)
        {
            bool ok = true;
            const string CloudSeed = ", \"Provider\": \"openai\", \"OpenAiBaseUrl\": \"https://api.openai.com/v1\", " +
                                     "\"CloudDataConsent\": true, \"CloudTextModel\": \"gpt-test-model\"";
            using (var rig = new StandDownRig("aibrain-remembrance-cloud", extraSeed: CloudSeed, liveBrainSeam: false))
            {
                ok &= Check(sb, "remembrance rig (cloud): the live brain is built from the settings, as shipped", rig.WaitForLiveBrain());
                AiBrain live = rig.Module.SessionForDiagnostics.LiveBrainForDiagnostics;
                FallbackBackend composite = live == null ? null : live.BackendForDiagnostics as FallbackBackend;
                ok &= Check(sb, "remembrance: a cloud slot with the default fallback is the composite, with the module's hold wired in",
                    composite != null && composite.LocalFallbackHold != null);
                int auditions = 0;
                rig.Module.AuditionBrainFactoryForDiagnostics = delegate(AiSettings s, int? keepAlive)
                {
                    auditions++;
                    return new AiBrain(new RecordingBackend("{\"text\":\"REMARK\",\"emotion\":\"neutral\"}", true), s.ActiveSlotSnapshot());
                };

                rig.Publish(BusyJson("transcribing", rig.Now));
                // The audition FIRST, so its press is the first reading since the publish: Init's reading was clear.
                string cloudAudition = rig.Module.PreviewDispositionAsync(false).GetAwaiter().GetResult();
                ok &= Check(sb, "remembrance: on a cloud slot Show me 5 examples goes ahead while Remembrance is busy",
                    auditions == 1 && cloudAudition != AiBrainModule.RemembranceBusyAnswer);
                ok &= Check(sb, "remembrance: a cloud audition's press reads the flag, so its own fallback is held back",
                    rig.Module.LocalFallbackHoldForDiagnostics() == "Remembrance is using the local model");
                ok &= Check(sb, "remembrance: on a cloud slot the drop goes ahead while Remembrance is busy, since the cloud is not the local GPU",
                    rig.Host.RaiseDrop(rig.Pet) && rig.Started.Count == 1);
                ok &= Check(sb, "remembrance: on a cloud slot that turn's fallback to the local slot is held back while Remembrance is busy",
                    composite != null && composite.LocalFallbackHold() == "Remembrance is using the local model");
                ok &= Check(sb, "remembrance: on a cloud slot the Status row reads On while Remembrance is busy",
                    rig.Status() == "On.");
                // A custom provider with no endpoint of its own, so the cloud test that runs answers before any request. The
                // Cloud provider card's button, which tests the cloud slot (lane feature/layout-aibrain split the two).
                PaneAction test = FindAction(rig.Pane, "Cloud provider", "Test connection");
                string cloudTest = test == null ? null : test.InvokeWithPendingAsync(
                    new Dictionary<string, string>(StringComparer.Ordinal) { { "cloudProvider", "custom" } }).GetAwaiter().GetResult();
                ok &= Check(sb, "remembrance: on a cloud slot Test connection is not refused while Remembrance is busy",
                    cloudTest != null && cloudTest != AiBrainModule.RemembranceBusyAnswer &&
                    cloudTest.StartsWith("✗", StringComparison.Ordinal));

                rig.Publish("");
                ok &= Check(sb, "WITNESS remembrance: with the flag cleared the cloud turn's fallback is allowed again",
                    rig.Host.RaisePokeResponders(rig.Pet) && rig.Started.Count == 2 &&
                    composite != null && composite.LocalFallbackHold() == null);
            }

            // The hold itself, on the composite: a retryable cloud failure falls over only while the hold allows it.
            Action<string> previousSink = AiBrain.LogSink;
            var lines = new List<string>();
            try
            {
                AiBrain.LogSink = delegate(string line) { lock (lines) lines.Add(line); };
                bool allowed = false;
                var local = new RecordingBackend("{\"text\":\"local\",\"emotion\":\"neutral\"}", true);
                using (var held = new FallbackBackend(new TransientFailBackend(), local, "cloud-vision", "local-text", "local-vision")
                {
                    LocalFallbackHold = delegate { return allowed ? null : "a test hold"; },
                })
                {
                    var messages = new List<ChatMessage> { ChatMessage.User("hello", null) };
                    bool threw = false;
                    try { held.ChatAsync("cloud-text", messages, true, CancellationToken.None).GetAwaiter().GetResult(); }
                    catch (AiBackendHttpException) { threw = true; }
                    ok &= Check(sb, "composite: a retryable cloud failure does NOT fall over to the local slot while the hold says no",
                        threw && local.ChatCalls == 0);
                    bool logged;
                    // The line names the hold's own reason since 1.4.0 (it said "Remembrance" whatever held it back).
                    lock (lines) logged = lines.Exists(delegate(string l) { return l.IndexOf("fallback held back: a test hold", StringComparison.Ordinal) >= 0; });
                    ok &= Check(sb, "composite: a held-back fallover says so in the log, naming the hold's reason", logged);
                    allowed = true;
                    string reply = held.ChatAsync("cloud-text", messages, true, CancellationToken.None).GetAwaiter().GetResult();
                    ok &= Check(sb, "WITNESS composite: with the hold allowing it the same failure falls over to the local slot",
                        local.ChatCalls == 1 && reply != null && reply.IndexOf("local", StringComparison.Ordinal) >= 0);
                }
            }
            finally { AiBrain.LogSink = previousSink; }
            return ok;
        }

        /// <summary>How many handlers a RecordingHost's ContextChanged holds, read from the event's backing field (a
        /// field-like event's compiler-generated field carries the event's name); -1 when it cannot be read.</summary>
        private static int ContextSubscribers(RecordingHost host)
        {
            FieldInfo field = typeof(RecordingHost).GetField("ContextChanged", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) return -1;
            Delegate handlers = field.GetValue(host) as Delegate;
            return handlers == null ? 0 : handlers.GetInvocationList().Length;
        }

        // A label-only FindAction stood here until lane feature/layout-aibrain gave two cards a "Test connection" each: by
        // label alone it answered whichever came first. The probe finds an action by card and label (AiEngineProbe.Cli.cs),
        // the way the host keys a button's result.

        /// <summary>
        /// One module instance for a Remembrance check: the shared offline seed (RA-072) plus any extra fields, a
        /// StandDownProbeBackend behind the live brain unless <c>liveBrainSeam</c> is false, the AskSink recording each
        /// started turn unless <c>sinkTurns</c> is false, and the module's clock. Disposing shuts the module down and
        /// puts the two process-globals back exactly as found, because --aibrain-selftest runs this probe from INSIDE a
        /// live module instance; a constructor that throws puts them back too.
        /// </summary>
        private sealed class StandDownRig : IDisposable
        {
            internal readonly StandDownHost Host = new StandDownHost();
            internal readonly AiBrainModule Module = new AiBrainModule();
            internal readonly StandDownProbeBackend Backend = new StandDownProbeBackend();
            internal readonly FakeCompanion Pet = new FakeCompanion(11, "eSheep");
            /// <summary>The module's clock (UtcNowForDiagnostics), which a check may move.</summary>
            internal DateTime Now = DateTime.UtcNow;
            private readonly List<KeyValuePair<string, bool>> _started = new List<KeyValuePair<string, bool>>();
            private readonly Action<string> _previousSink;
            private readonly string _previousRoot;
            private readonly TempModuleStorage _storage;
            private bool _live;

            internal StandDownRig(string name, bool sinkTurns = true, string extraSeed = "",
                string publishBeforeInit = null, bool liveBrainSeam = true)
            {
                _previousSink = AiBrain.LogSink;
                _previousRoot = AiPaths.CurrentRootForDiagnostics;
                _storage = new TempModuleStorage(name);
                try
                {
                    SeedOfflineModuleSettings(_storage, extraSeed);
                    Host.UseStorage("aibrain", _storage);
                    Host.Declared = Module.Info.Permissions;
                    StandDownProbeBackend backend = Backend;
                    if (liveBrainSeam)
                        Module.BrainFactoryForDiagnostics = delegate(AiSettings s) { return new AiBrain(backend, s.ActiveSlotSnapshot()); };
                    Module.UtcNowForDiagnostics = delegate { return Now; };
                    if (sinkTurns)
                    {
                        Module.AskSinkForDiagnostics = delegate(ScreenContext ctx, string zone, bool allowVision, ICompanion pet)
                        {
                            lock (_started)
                                _started.Add(new KeyValuePair<string, bool>(
                                    pet == null ? "(none)" : pet.Id.ToString(CultureInfo.InvariantCulture), allowVision));
                            return Task.CompletedTask;
                        };
                    }
                    if (publishBeforeInit != null) Host.PublishContext("remembrance", RemembranceBusyFlag.Key, publishBeforeInit);
                    Module.Init(Host);
                    _live = true;
                    Host.RaiseCompanionSpawned(Pet);   // the tray row asks about the last pet seen
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            internal List<KeyValuePair<string, bool>> Started
            {
                get { lock (_started) return new List<KeyValuePair<string, bool>>(_started); }
            }

            internal OptionsPane Pane { get { return Host.OptionsPanes.Count == 1 ? Host.OptionsPanes[0] : null; } }

            /// <summary>ApplyState builds the brain on a pool thread; wait for one, briefly.</summary>
            internal bool WaitForLiveBrain()
            {
                return SpinWait.SpinUntil(delegate { return Module.SessionForDiagnostics.LiveBrainForDiagnostics != null; },
                    TimeSpan.FromSeconds(5));
            }

            /// <summary>Wait until the session holds no brain (AI switched off has retired it), briefly.</summary>
            internal bool WaitForNoBrain()
            {
                return SpinWait.SpinUntil(delegate { return Module.SessionForDiagnostics.LiveBrainForDiagnostics == null; },
                    TimeSpan.FromSeconds(5));
            }

            /// <summary>Press Apply with nothing changed: ApplyState rebuilds the brain.</summary>
            internal void Apply()
            {
                OptionsPane pane = Pane;
                if (pane != null) pane.Save(new Dictionary<string, string>(StringComparer.Ordinal));
            }

            /// <summary>Apply, then wait until the session holds a brain other than the one it held: by then the old one
            /// has been retired, its eviction included, and the counting double has seen it.</summary>
            internal bool ApplyAndWaitForNewBrain()
            {
                AiBrain before = Module.SessionForDiagnostics.LiveBrainForDiagnostics;
                Apply();
                return SpinWait.SpinUntil(delegate
                {
                    AiBrain now = Module.SessionForDiagnostics.LiveBrainForDiagnostics;
                    return now != null && !ReferenceEquals(now, before);
                }, TimeSpan.FromSeconds(5));
            }

            /// <summary>Publish under the flag's key, as Remembrance does.</summary>
            internal void Publish(string valueJson)
            {
                Host.PublishContext("remembrance", RemembranceBusyFlag.Key, valueJson);
            }

            /// <summary>The pane's Status row, as a Load right now reads it: its first part, BrainStatusLine's answer. Since
            /// aibrain 1.3.0 the row is the Status card's whole line, the engine, vision and the last remark following
            /// after "  |  " (feature/cli-backend), and these checks are about the first part.</summary>
            internal string Status()
            {
                OptionsPane pane = Pane;
                if (pane == null) return "(no pane)";
                string status;
                if (!pane.Load().TryGetValue("brainStatus", out status)) return "(no status row)";
                int bar = status.IndexOf("  |  ", StringComparison.Ordinal);
                return bar >= 0 ? status.Substring(0, bar) : status;
            }

            internal int CountLog(string fragment)
            {
                int count = 0;
                foreach (string line in Host.LoggedLines)
                    if (line != null && line.IndexOf(fragment, StringComparison.Ordinal) >= 0) count++;
                return count;
            }

            /// <summary>How many times <paramref name="text"/> was said to <paramref name="pet"/> itself.</summary>
            internal int SaidTo(ICompanion pet, string text)
            {
                int count = 0;
                foreach (KeyValuePair<ICompanion, string> said in Host.SaidToCompanions)
                    if (ReferenceEquals(said.Key, pet) && string.Equals(said.Value, text, StringComparison.Ordinal)) count++;
                return count;
            }

            public void Dispose()
            {
                if (_live)
                {
                    _live = false;
                    try { Module.Shutdown(); } catch { }
                }
                AiBrain.LogSink = _previousSink;
                AiPaths.SwapRoot(_previousRoot);
                _storage.Dispose();
            }
        }
    }
}
