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
    /// builds the brain without PrepareAsync, and put the local slot on the OpenAI-compatible protocol, so
    /// retiring that brain at Shutdown is a no-op rather than a keep_alive:0 request to whatever Ollama this
    /// machine is running; and <see cref="AiBrainModule.AskSinkForDiagnostics"/> receives each started turn in
    /// place of AskCoreAsync.
    /// </summary>
    public static partial class AiEngineProbe
    {
        internal static bool RunModule(StringBuilder sb)
        {
            bool ok = true;
            ok &= CheckModuleEntryPoints(sb);
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
                // so this run can never inherit a real provider, key or consent.
                File.WriteAllText(
                    Path.Combine(storage.DataDirectory, "ai-settings.json"),
                    "{ \"SchemaVersion\": " + AiSettings.CurrentSchemaVersion + ", \"AiBrainEnabled\": true, " +
                    "\"AutoStartServer\": false, \"LocalBackendKind\": \"openai-compat\", \"UseVision\": true }",
                    new UTF8Encoding(false));

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

                var pet = new FakeCompanion(7, "eSheep");
                host.RaiseCompanionSpawned(pet);   // the tray row has no pet of its own; it asks about the last one seen

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
    }
}
