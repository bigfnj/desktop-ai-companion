using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DesktopAICompanion.Ai;
using DesktopAICompanion.CodingAgent;
using DesktopAICompanion.ModuleKit.Testing;   // RecordingHost, TempModuleStorage, FakeCompanion
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.AiBrainModule
{
    /// <summary>
    /// Lane feature/cli-backend (aibrain 1.3.0): the coding-agent CLI backend. First the shared runner's own self-check
    /// (shared/CodingAgentCli, which Remembrance's self-test runs too, so each payload proves the copy it ships), then this
    /// module's use of it: each of the four "Brain runs on" states builds the brain it names, every entry point keeps
    /// working and nothing stands down for Remembrance on a CLI, the persona reaches the CLI's system prompt, a CLI failure
    /// is not retried, an existing settings file keeps its slot, and the pane greys, refuses, reports and validates as the
    /// owner's approved mockup (AB2) and the Status card ask. No CLI is started, no model is called and no screen is
    /// captured: the runner's process seam is a fake, every binary an empty file in a scratch tree, every image a one-pixel
    /// PNG, and the entry points end in AskSinkForDiagnostics.
    /// </summary>
    public static partial class AiEngineProbe
    {
        internal static bool RunCli(StringBuilder sb)
        {
            bool ok = true;
            CodingAgentCliSelfCheck.Run(delegate(string name, bool condition) { ok &= Check(sb, name, condition); });
            ok &= GuardedCheck(sb, "CheckCliSettings", CheckCliSettings);
            ok &= GuardedCheck(sb, "CheckCliRouting", CheckCliRouting);
            ok &= GuardedCheck(sb, "CheckCliPersonaAndImage", CheckCliPersonaAndImage);
            ok &= GuardedCheck(sb, "CheckCliEntryPoints", CheckCliEntryPoints);
            ok &= GuardedCheck(sb, "CheckCliPaneLayout", CheckCliPaneLayout);
            ok &= GuardedCheck(sb, "CheckCliPaneChoice", CheckCliPaneChoice);
            ok &= GuardedCheck(sb, "CheckCliCardAndStatus", CheckCliCardAndStatus);
            ok &= GuardedCheck(sb, "CheckCliPaneActions", CheckCliPaneActions);
            return ok;
        }

        private const string CliReply = "{\"text\":\"A CLI REMARK\",\"emotion\":\"happy\"}";

        /// <summary>The settings fields and an existing file: a file without the key keeps its slot, an unknown id reads
        /// as no CLI.</summary>
        private static bool CheckCliSettings(StringBuilder sb)
        {
            bool ok = true;
            string previousRoot = AiPaths.CurrentRootForDiagnostics;
            string root = Path.Combine(Path.GetTempPath(), "dp-aibrain-cli-settings-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                AiPaths.SwapRoot(root);
                string file = Path.Combine(root, "ai-settings.json");
                // A 1.2.0 file: no CliBackend key at all, the local slot, then a cloud slot.
                File.WriteAllText(file, "{ \"SchemaVersion\": " + AiSettings.CurrentSchemaVersion + ", \"AiBrainEnabled\": true, " +
                    "\"LocalBackendKind\": \"openai-compat\", \"Endpoint\": \"http://127.0.0.1:9\" }", new UTF8Encoding(false));
                AiSettings local = AiSettings.Load();
                using (AiBrain localBrain = AiBrainModule.CreateBrain(local))
                    ok &= Check(sb, "aibrain cli: a settings file written before 1.3.0 has no CLI and keeps its local slot",
                        local.CliBackend == "" && !AiBrainModule.IsCliSlot(local) && !(localBrain.BackendForDiagnostics is CodingAgentBackend) &&
                        AiBrainModule.BrainRunsOnLabel(local) == AiBrainModule.BrainRunsOnLocal);
                File.WriteAllText(file, "{ \"SchemaVersion\": " + AiSettings.CurrentSchemaVersion + ", \"AiBrainEnabled\": true, \"Provider\": \"openai\", " +
                    "\"OpenAiBaseUrl\": \"https://api.openai.com/v1\", \"CloudTextModel\": \"gpt-x\", \"CloudDataConsent\": true, " +
                    "\"UseLocalFallback\": false }", new UTF8Encoding(false));
                AiSettings cloud = AiSettings.Load();
                using (AiBrain cloudBrain = AiBrainModule.CreateBrain(cloud))
                    ok &= Check(sb, "aibrain cli: ...and a cloud slot written before 1.3.0 keeps its cloud backend",
                        cloud.CliBackend == "" && cloudBrain.BackendForDiagnostics is OpenAiCompatBackend &&
                        AiBrainModule.BrainRunsOnLabel(cloud) == AiBrainModule.BrainRunsOnCloud);
                File.WriteAllText(file, "{ \"SchemaVersion\": " + AiSettings.CurrentSchemaVersion + ", \"CliBackend\": \" Codex \" }", new UTF8Encoding(false));
                ok &= Check(sb, "aibrain cli: a stored CLI id is read whatever its case and spacing", AiSettings.Load().CliBackend == "codex");
                File.WriteAllText(file, "{ \"SchemaVersion\": " + AiSettings.CurrentSchemaVersion + ", \"CliBackend\": \"gemini\", " +
                    "\"LastCloudProvider\": \"nonesuch\" }", new UTF8Encoding(false));
                AiSettings unknown = AiSettings.Load();
                ok &= Check(sb, "aibrain cli: an id this version does not know reads as no CLI, the conservative slot",
                    unknown.CliBackend == "" && unknown.LastCloudProvider == "");
                var chosen = new AiSettings { CliBackend = "claude" };
                ok &= Check(sb, "aibrain cli: a CLI slot's brain resolves the CLI's name, never a model of the user's",
                    chosen.ActiveSlotSnapshot().TextModel == "claude-code-cli" && chosen.ActiveSlotSnapshot().VisionModel == "claude-code-cli");
                ok &= Check(sb, "aibrain cli: the CLI choice is part of the backend fingerprint",
                    AiBrainModule.BackendFingerprint(new AiSettings()) != AiBrainModule.BackendFingerprint(chosen));
            }
            finally
            {
                AiPaths.SwapRoot(previousRoot);
                try { Directory.Delete(root, true); } catch { }
            }
            return ok;
        }

        /// <summary>The brain CreateBrain builds for these settings, as (backend, host description), or (null, the exception's
        /// type) when the build throws: every routing check below is judged on its own, so a build that throws is THAT
        /// check's failure and not a throw that names the group instead.</summary>
        private static KeyValuePair<ICompanionBrainBackend, string> BuildFor(AiSettings s, CodingAgentCli runner)
        {
            try
            {
                using (AiBrain brain = AiBrainModule.CreateBrain(s, null, null, runner))
                    return new KeyValuePair<ICompanionBrainBackend, string>(brain.BackendForDiagnostics, brain.BackendHostDescription);
            }
            catch (Exception ex) { return new KeyValuePair<ICompanionBrainBackend, string>(null, ex.GetType().Name); }
        }

        /// <summary>Each CLI state builds the brain it names, over the runner it is handed, ahead of whatever slot is under it.</summary>
        private static bool CheckCliRouting(StringBuilder sb)
        {
            bool ok = true;
            var runner = new CodingAgentCli(null, null);
            KeyValuePair<ICompanionBrainBackend, string> claude = BuildFor(new AiSettings { CliBackend = "claude" }, runner);
            CodingAgentBackend claudeBackend = claude.Key as CodingAgentBackend;
            ok &= Check(sb, "aibrain cli: Claude Code CLI builds the brain on the Claude Code backend, over the runner it is handed",
                claudeBackend != null && claudeBackend.AgentForDiagnostics == CodingAgentKind.Claude && claudeBackend.CliForDiagnostics == runner &&
                claude.Value == "claude-cli");
            KeyValuePair<ICompanionBrainBackend, string> codex = BuildFor(
                new AiSettings { CliBackend = "codex", Provider = "openai", OpenAiBaseUrl = "https://api.openai.com/v1", CloudDataConsent = true }, runner);
            CodingAgentBackend codexBackend = codex.Key as CodingAgentBackend;
            ok &= Check(sb, "aibrain cli: Codex CLI builds the brain on the Codex backend, ahead of a cloud provider also chosen",
                codexBackend != null && codexBackend.AgentForDiagnostics == CodingAgentKind.Codex && codexBackend.CliForDiagnostics == runner);
            KeyValuePair<ICompanionBrainBackend, string> local = BuildFor(
                new AiSettings { LocalBackendKind = "openai-compat", Endpoint = "http://127.0.0.1:9" }, runner);
            ok &= Check(sb, "WITNESS aibrain cli: the local slot is not a CLI backend", local.Key != null && !(local.Key is CodingAgentBackend));
            KeyValuePair<ICompanionBrainBackend, string> beforeChecks = BuildFor(new AiSettings
            {
                CliBackend = "claude", Endpoint = "not a url", Provider = "openai", OpenAiBaseUrl = "http://insecure.example/v1",
                CloudDataConsent = false,
            }, runner);
            ok &= Check(sb, "aibrain cli: a CLI slot's brain is built before any endpoint or consent check of the slots under it (" +
                (beforeChecks.Key == null ? "threw " : "built ") + beforeChecks.Value + ")",
                beforeChecks.Key is CodingAgentBackend);
            ok &= Check(sb, "aibrain cli: a CLI slot needs no endpoint, model or consent to start",
                AiBrainModule.CanUse(new AiSettings { CliBackend = "codex", Endpoint = "not a url", CloudDataConsent = false }, out _));
            ok &= Check(sb, "aibrain cli: the radio's four options are the mockup's, the CLIs by the runner's own labels",
                string.Join("|", AiBrainModule.BrainRunsOnLabels()) ==
                    "Local model|Cloud provider|" + CodingAgents.ChoiceLabel(CodingAgentKind.Claude) + "|" + CodingAgents.ChoiceLabel(CodingAgentKind.Codex) &&
                AiBrainModule.OnCliOnly == "brainRunsOn=" + CodingAgents.ChoiceLabel(CodingAgentKind.Claude) + "|" +
                    CodingAgents.ChoiceLabel(CodingAgentKind.Codex));
            return ok;
        }

        private static AiSettings PersonaSettings(string cli, bool vision)
        {
            return new AiSettings
            {
                CliBackend = cli, CompanionName = "Bramble", UserName = "Quill", Disposition = "samuel", UseVision = vision,
            };
        }

        /// <summary>The persona reaches the CLI's system prompt, the screen its stdin, the image the CLI's own way in, and a
        /// failure is not retried.</summary>
        private static bool CheckCliPersonaAndImage(StringBuilder sb)
        {
            bool ok = true;
            using (var scratch = new FakeCliScratch())
            {
                var log = new List<string>();
                var fake = new FakeCliProcess { Respond = FakeCliProcess.Answering(CliReply) };
                CodingAgentCli runner = scratch.NewRunner(fake, log);
                string disposition = Dispositions.InstructionForId("samuel");

                using (AiBrain brain = AiBrainModule.CreateBrain(PersonaSettings("claude", false), null, null, runner))
                {
                    BrainResponse said = brain.GenerateWithRepeatGuardAsync("claude-code-cli", "SYNTHETIC-SCREEN-551 a code editor",
                        null, CancellationToken.None).GetAwaiter().GetResult();
                    FakeCliCall call = fake.Calls.Find(delegate(FakeCliCall c) { return c.IsModelCall; });
                    string system = call == null ? "" : call.After("--system-prompt") ?? "";
                    ok &= Check(sb, "aibrain cli: the persona is Claude Code's system prompt, name, user and disposition included",
                        system.Contains("You are Bramble") && system.Contains("Quill") && system.Contains(disposition));
                    ok &= Check(sb, "aibrain cli: what is on screen goes on Claude Code's stdin",
                        call != null && call.Input.Contains("SYNTHETIC-SCREEN-551") &&
                        !call.Arguments.Exists(delegate(string a) { return a.Contains("SYNTHETIC-SCREEN-551"); }));
                    ok &= Check(sb, "aibrain cli: Claude Code's reply is parsed into the remark the companion says",
                        said != null && said.Text == "A CLI REMARK" && said.Emotion == "happy");
                }

                fake.Clear();
                string png = Convert.ToBase64String(CodingAgentCliSelfCheck.SyntheticPng);
                using (AiBrain brain = AiBrainModule.CreateBrain(PersonaSettings("codex", true), null, null, runner))
                {
                    BrainResponse said = brain.GenerateWithRepeatGuardAsync("codex-cli", "Look at my screen and react.",
                        new[] { png }, CancellationToken.None).GetAwaiter().GetResult();
                    FakeCliCall call = fake.Calls.Find(delegate(FakeCliCall c) { return c.IsModelCall; });
                    ok &= Check(sb, "aibrain cli: the persona is Codex's instructions file, name, user and disposition included",
                        call != null && call.InstructionsAtCall != null && call.InstructionsAtCall.Contains("You are Bramble") &&
                        call.InstructionsAtCall.Contains("Quill") && call.InstructionsAtCall.Contains(disposition));
                    ok &= Check(sb, "aibrain cli: a vision turn hands Codex the screenshot's bytes and its image-capable pick",
                        call != null && call.ImageAtCall != null && Convert.ToBase64String(call.ImageAtCall) == png &&
                        call.After("-m") == "vision-second");
                    ok &= Check(sb, "aibrain cli: Codex's reply is parsed into the remark", said != null && said.Text == "A CLI REMARK");
                }

                fake.Clear();
                using (AiBrain brain = AiBrainModule.CreateBrain(PersonaSettings("claude", true), null, null, runner))
                {
                    brain.GenerateWithRepeatGuardAsync("claude-code-cli", "Look at my screen and react.",
                        new[] { png }, CancellationToken.None).GetAwaiter().GetResult();
                    FakeCliCall call = fake.Calls.Find(delegate(FakeCliCall c) { return c.IsModelCall; });
                    ok &= Check(sb, "aibrain cli: a vision turn hands Claude Code the screenshot inline",
                        call != null && call.Input.Contains("\"media_type\":\"image/png\"") && call.Input.Contains(png));
                }

                fake.Clear();
                fake.Respond = delegate(FakeCliCall c, CancellationToken token)
                {
                    return c.IsModelCall
                        ? Task.FromResult(FakeCliProcess.Result(1, FakeCliProcess.ClaudeStream("Not logged in · Please run /login", true), ""))
                        : FakeCliProcess.Answering(CliReply)(c, token);
                };
                Exception thrown = null;
                using (AiBrain brain = AiBrainModule.CreateBrain(PersonaSettings("claude", false), null, null, runner))
                {
                    try { brain.GenerateWithRepeatGuardAsync("claude-code-cli", "text", null, CancellationToken.None).GetAwaiter().GetResult(); }
                    catch (Exception ex) { thrown = ex; }
                }
                ok &= Check(sb, "aibrain cli: a CLI failure is one call, never retried, and logged by its class",
                    thrown is CodingAgentCliException && fake.Calls.FindAll(delegate(FakeCliCall c) { return c.IsModelCall; }).Count == 1 &&
                    AiBrain.DescribeError(thrown) == "cli-not-signed-in");
            }
            return ok;
        }

        /// <summary>A module instance for these checks: the shared offline seed plus any extra fields, a runner over a fake
        /// CLI handed in before Init, and the AskSink recording every started turn.</summary>
        private sealed class CliRig : IDisposable
        {
            internal readonly RecordingHost Host = new RecordingHost();
            internal readonly AiBrainModule Module = new AiBrainModule();
            internal readonly FakeCompanion Pet = new FakeCompanion(21, "eSheep");
            internal readonly FakeCliScratch Scratch = new FakeCliScratch();
            internal readonly FakeCliProcess Fake = new FakeCliProcess { Respond = FakeCliProcess.Answering("OK") };
            internal readonly List<string> RunnerLog = new List<string>();
            internal readonly CodingAgentCli Runner;
            private readonly List<bool> _started = new List<bool>();
            private readonly Action<string> _previousSink;
            private readonly string _previousRoot;
            private readonly TempModuleStorage _storage;
            private bool _live;

            internal CliRig(string name, string extraSeed)
            {
                _previousSink = AiBrain.LogSink;
                _previousRoot = AiPaths.CurrentRootForDiagnostics;
                _storage = new TempModuleStorage(name);
                try
                {
                    SeedOfflineModuleSettings(_storage, extraSeed);
                    Host.UseStorage("aibrain", _storage);
                    Host.Declared = Module.Info.Permissions;
                    Runner = Scratch.NewRunner(Fake, RunnerLog);
                    Module.CliForDiagnostics = Runner;
                    Module.AskSinkForDiagnostics = delegate(ScreenContext ctx, string zone, bool allowVision, ICompanion pet)
                    {
                        lock (_started) _started.Add(allowVision);
                        return Task.CompletedTask;
                    };
                    Module.Init(Host);
                    _live = true;
                    Host.RaiseCompanionSpawned(Pet);
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            internal int Started { get { lock (_started) return _started.Count; } }

            internal OptionsPane Pane { get { return Host.OptionsPanes.Count == 1 ? Host.OptionsPanes[0] : null; } }

            internal AiSettings Settings { get { return Module.SettingsForDiagnostics; } }

            internal bool Save(params string[] pairs)
            {
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int i = 0; i + 1 < pairs.Length; i += 2) values[pairs[i]] = pairs[i + 1];
                OptionsPane pane = Pane;
                return pane != null && pane.Save(values);
            }

            public void Dispose()
            {
                try { if (_live) Module.Shutdown(); } catch { }
                _live = false;
                AiBrain.LogSink = _previousSink;
                AiPaths.SwapRoot(_previousRoot);
                try { _storage.Dispose(); } catch { }
                Scratch.Dispose();
            }
        }

        /// <summary>Every entry point on a CLI: the drop, the poke and the tray row (the hotkey's call) all start their turn
        /// while Remembrance is transcribing, because nothing on this path loads into Ollama; and the live brain is the
        /// module's own runner's.</summary>
        private static bool CheckCliEntryPoints(StringBuilder sb)
        {
            bool ok = true;
            using (var rig = new CliRig("aibrain-cli-entry", ", \"CliBackend\": \"claude\""))
            {
                bool built = SpinWait.SpinUntil(delegate { return rig.Module.SessionForDiagnostics.LiveBrainForDiagnostics != null; },
                    TimeSpan.FromSeconds(5));
                AiBrain live = rig.Module.SessionForDiagnostics.LiveBrainForDiagnostics;
                CodingAgentBackend backend = live == null ? null : live.BackendForDiagnostics as CodingAgentBackend;
                ok &= Check(sb, "aibrain cli: the live brain runs on the module's own runner, the one Validate uses",
                    built && backend != null && backend.CliForDiagnostics == rig.Module.CliRunnerForDiagnostics &&
                    rig.Module.CliRunnerForDiagnostics == rig.Runner);

                rig.Host.PublishContext("remembrance", RemembranceBusyFlag.Key, BusyJson("transcribing", DateTime.UtcNow));
                bool drop = rig.Host.RaiseDrop(rig.Pet);
                bool poke = rig.Host.RaisePokeResponders(rig.Pet);
                // The hotkey is registered and calls what the tray row calls (Ask(null, true)); RecordingHost keeps no
                // handler to press, so the tray row stands for both.
                bool tray = ClickTray(rig.Host, "Ask about my screen");
                bool hotkey = rig.Host.RegisteredHotkeys.Count == 1;
                ok &= Check(sb, "aibrain cli: on a CLI the drop, the poke and the tray row (the hotkey's call) all start their turn while Remembrance transcribes",
                    drop && poke && tray && hotkey && rig.Started == 3);
                ok &= Check(sb, "aibrain cli: ...with nothing declined or said for Remembrance, since nothing loads into Ollama",
                    !rig.Host.LoggedLines.Exists(delegate(string l) { return l.Contains("remembrance stand-down"); }) &&
                    !rig.Host.SaidLines.Exists(delegate(string l) { return l.Contains("Remembrance is using the model"); }));
                ok &= Check(sb, "aibrain cli: ...and the Status card says the brain is on, not standing down",
                    rig.Pane != null && rig.Pane.Load()["brainStatus"].StartsWith("On.  |  runs on: Claude Code CLI", StringComparison.Ordinal));
            }
            return ok;
        }

        private static readonly string[] LocalOrCloudFields =
        {
            "localBackendKind", "endpoint", "textModel", "visionModel", "autoStart", "standDownRemembrance", "residency", "vramStatus",
        };

        private static readonly string[] CloudOnlyFields =
        {
            "cloudProvider", "cloudEndpoint", "apiKey", "cloudTextModel", "cloudVisionModel", "cloudConsent", "useLocalFallback",
        };

        private static readonly string[] CliCardRows = { "cliName", "cliAccount", "cliStatus", "cliSends" };

        private static readonly string[] AlwaysLiveFields =
        {
            "brainStatus", "enabled", "brainRunsOn", "companionName", "userName", "disposition", "hotkey", "useVision",
            "tesseractPath", "standDownFullscreen",
        };

        private static SettingField FieldOf(OptionsPane pane, string id)
        {
            if (pane == null || pane.Schema == null) return null;
            foreach (SettingField f in pane.Schema) if (f != null && f.Id == id) return f;
            return null;
        }

        /// <summary>The cards in the approved mockup's order (AB2) with the Status card first, and the greying.</summary>
        private static bool CheckCliPaneLayout(StringBuilder sb)
        {
            bool ok = true;
            using (var rig = new CliRig("aibrain-cli-layout", ""))
            {
                OptionsPane pane = rig.Pane;
                var groups = new List<string>();
                if (pane != null && pane.Schema != null)
                    foreach (SettingField f in pane.Schema)
                        if (f != null && !groups.Contains(f.Group)) groups.Add(f.Group);
                ok &= Check(sb, "aibrain cli: the cards come in the approved mockup's order, the Status card first: " + string.Join(", ", groups),
                    string.Join("|", groups) == "Status|AI brain|" + AiBrainModule.CliCardGroup +
                        "|Persona|Triggers|What it sees|Local provider|Local server (Ollama only)|Cloud provider");
                SettingField status = FieldOf(pane, "brainStatus");
                ok &= Check(sb, "aibrain cli: the Status card is one Info line, full width and pinned first",
                    status != null && status.Kind == SettingKind.Info && status.Group == "Status" && status.FullWidth && status.PinTop);
                SettingField enabled = FieldOf(pane, "enabled");
                SettingField cliName = FieldOf(pane, "cliName");
                ok &= Check(sb, "aibrain cli: the AI brain card and the CLI card are pinned beside it",
                    enabled != null && enabled.PinTop && enabled.Group == "AI brain" && cliName != null && cliName.PinTop);
                SettingField radio = FieldOf(pane, "brainRunsOn");
                ok &= Check(sb, "aibrain cli: \"Brain runs on\" is a radio of its four options in the AI brain card",
                    radio != null && radio.Kind == SettingKind.Radio && radio.Group == "AI brain" &&
                    string.Join("|", radio.Options) == string.Join("|", AiBrainModule.BrainRunsOnLabels()));
                SettingField vision = FieldOf(pane, "useVision");
                ok &= Check(sb, "aibrain cli: Use vision sits in What it sees, beside the OCR engine, and Fallback in Cloud provider",
                    vision != null && vision.Group == "What it sees" && FieldOf(pane, "tesseractPath") != null &&
                    FieldOf(pane, "tesseractPath").Group == "What it sees" && FieldOf(pane, "useLocalFallback") != null &&
                    FieldOf(pane, "useLocalFallback").Group == "Cloud provider");
                SettingField provider = FieldOf(pane, "cloudProvider");
                ok &= Check(sb, "aibrain cli: the cloud dropdown no longer offers \"(none)\"; the radio says whether the cloud is used",
                    provider != null && Array.IndexOf(provider.Options, "(none)") < 0 && provider.Options.Length == 3);

                var wrong = new List<string>();
                foreach (string id in LocalOrCloudFields)
                {
                    SettingField f = FieldOf(pane, id);
                    if (f == null || f.EnabledWhen != "brainRunsOn=Local model|Cloud provider") wrong.Add(id);
                }
                ok &= Check(sb, "aibrain cli: the local slot's settings grey unless the brain runs on the local model or the cloud (its fallback)" +
                    (wrong.Count > 0 ? ": " + string.Join(", ", wrong) : ""), wrong.Count == 0);
                wrong.Clear();
                foreach (string id in CloudOnlyFields)
                {
                    SettingField f = FieldOf(pane, id);
                    if (f == null || f.EnabledWhen != "brainRunsOn=Cloud provider") wrong.Add(id);
                }
                ok &= Check(sb, "aibrain cli: the cloud provider's settings grey unless the brain runs on the cloud" +
                    (wrong.Count > 0 ? ": " + string.Join(", ", wrong) : ""), wrong.Count == 0);
                wrong.Clear();
                foreach (string id in CliCardRows)
                {
                    SettingField f = FieldOf(pane, id);
                    if (f == null || f.Kind != SettingKind.Info || f.Group != AiBrainModule.CliCardGroup ||
                        f.EnabledWhen != "brainRunsOn=Claude Code CLI|Codex CLI") wrong.Add(id);
                }
                ok &= Check(sb, "aibrain cli: the CLI card's rows grey unless a CLI is chosen, in the mockup's order" +
                    (wrong.Count > 0 ? ": " + string.Join(", ", wrong) : ""),
                    wrong.Count == 0 && IndexOfField(pane, "cliName") < IndexOfField(pane, "cliAccount") &&
                    IndexOfField(pane, "cliAccount") < IndexOfField(pane, "cliStatus") &&
                    IndexOfField(pane, "cliStatus") < IndexOfField(pane, "cliSends"));
                wrong.Clear();
                foreach (string id in AlwaysLiveFields)
                {
                    SettingField f = FieldOf(pane, id);
                    if (f == null || !string.IsNullOrEmpty(f.EnabledWhen)) wrong.Add(id);
                }
                ok &= Check(sb, "WITNESS aibrain cli: the status, the switch, the radio, persona, triggers, vision, OCR and the fullscreen stand-down never grey" +
                    (wrong.Count > 0 ? ": " + string.Join(", ", wrong) : ""), wrong.Count == 0);
            }
            return ok;
        }

        private static int IndexOfField(OptionsPane pane, string id)
        {
            if (pane == null || pane.Schema == null) return -1;
            for (int i = 0; i < pane.Schema.Count; i++) if (pane.Schema[i] != null && pane.Schema[i].Id == id) return i;
            return -1;
        }

        /// <summary>What the radio stores, and that switching between the engines loses nothing.</summary>
        private static bool CheckCliPaneChoice(StringBuilder sb)
        {
            bool ok = true;
            using (var rig = new CliRig("aibrain-cli-choice",
                ", \"Provider\": \"openrouter\", \"OpenAiBaseUrl\": \"https://openrouter.ai/api/v1\", \"CloudDataConsent\": true, \"CloudTextModel\": \"or-model\""))
            {
                OptionsPane pane = rig.Pane;
                ok &= Check(sb, "aibrain cli: a cloud install shows Cloud provider and its provider", pane.Load()["brainRunsOn"] == "Cloud provider" &&
                    pane.Load()["cloudProvider"] == "openrouter");
                rig.Save("brainRunsOn", "Codex CLI", "cloudProvider", "openrouter");
                ok &= Check(sb, "aibrain cli: choosing Codex CLI stores its id and leaves the cloud slot under it as it was",
                    rig.Settings.CliBackend == "codex" && rig.Settings.Provider == "openrouter" && AiSettings.Load().CliBackend == "codex" &&
                    pane.Load()["brainRunsOn"] == "Codex CLI");
                rig.Save("brainRunsOn", "");
                ok &= Check(sb, "aibrain cli: text that is no option changes nothing", rig.Settings.CliBackend == "codex");
                rig.Save("brainRunsOn", "Cloud provider", "cloudProvider", "openrouter");
                ok &= Check(sb, "WITNESS aibrain cli: choosing Cloud provider again is the cloud slot it was",
                    rig.Settings.CliBackend == "" && rig.Settings.Provider == "openrouter" && rig.Settings.OpenAiBaseUrl == "https://openrouter.ai/api/v1");
                rig.Save("brainRunsOn", "Local model", "cloudProvider", "openrouter");
                ok &= Check(sb, "aibrain cli: choosing Local model clears the cloud primary and remembers the provider",
                    rig.Settings.Provider == "" && rig.Settings.LastCloudProvider == "openrouter" && pane.Load()["brainRunsOn"] == "Local model" &&
                    pane.Load()["cloudProvider"] == "openrouter");
                rig.Save("brainRunsOn", "Cloud provider");
                ok &= Check(sb, "aibrain cli: choosing Cloud provider with no dropdown value restores the remembered provider, never the local slot",
                    rig.Settings.Provider == "openrouter" && pane.Load()["brainRunsOn"] == "Cloud provider");
                rig.Save("cloudProvider", "(none)");
                ok &= Check(sb, "WITNESS aibrain cli: a caller that hands over no radio still means local by the old \"(none)\"",
                    rig.Settings.Provider == "" && rig.Settings.CliBackend == "");
            }
            return ok;
        }

        /// <summary>The CLI card's rows and the Status card's line.</summary>
        private static bool CheckCliCardAndStatus(StringBuilder sb)
        {
            bool ok = true;
            using (var rig = new CliRig("aibrain-cli-card", ""))
            {
                OptionsPane pane = rig.Pane;
                IReadOnlyDictionary<string, string> shown = pane.Load();
                ok &= Check(sb, "aibrain cli: with no CLI chosen the CLI row says to pick one",
                    shown["cliName"].StartsWith("None chosen.", StringComparison.Ordinal));
                rig.Runner.RefreshDetailsAsync(CodingAgentKind.Claude, CancellationToken.None).GetAwaiter().GetResult();
                rig.Save("brainRunsOn", "Claude Code CLI", "useVision", "true");
                shown = pane.Load();
                ok &= Check(sb, "aibrain cli: the card's CLI row names Claude Code, its version and its default model",
                    shown["cliName"] == "Claude Code 2.1.292, its default model");
                ok &= Check(sb, "aibrain cli: the card's Signed in as row names the account the CLI reports",
                    shown["cliAccount"] == "someone@example.invalid (max)");
                ok &= Check(sb, "aibrain cli: the card's Status row asks for a Validate until one has run",
                    shown["cliStatus"] == "Not validated yet. Press Validate.");
                string sends = shown["cliSends"];
                ok &= Check(sb, "aibrain cli: Goes through it names the vendor, every remark path and the screenshot when vision is on",
                    sends.Contains("Anthropic") && sends.Contains("Ask") && sends.Contains("the hotkey") && sends.Contains("the tray row") &&
                    sends.Contains("the random drops") && sends.Contains("a screenshot of the window (Use vision is on)") && sends.Contains("auditions"));
                ok &= Check(sb, "WITNESS aibrain cli: with vision off, Goes through it says the screen's text goes instead",
                    AiBrainModule.CliSendsLine(new AiSettings { UseVision = false }, CodingAgentKind.Codex).Contains("OpenAI") &&
                    AiBrainModule.CliSendsLine(new AiSettings { UseVision = false }, CodingAgentKind.Codex).Contains("the text read off the screen (OCR)"));
                ok &= Check(sb, "aibrain cli: the card names Codex's pick, and the screenshot one too when vision is on and they differ",
                    AiBrainModule.CodexModelPhrase(new CodingAgentCli.CliDetails { TextModel = "a", VisionModel = "b" }, true) ==
                        "a, and b for a screenshot: the first models this Codex lists" &&
                    AiBrainModule.CodexModelPhrase(new CodingAgentCli.CliDetails { TextModel = "a", VisionModel = "b" }, false) ==
                        "a, the first model this Codex lists");

                string status = shown["brainStatus"];
                ok &= Check(sb, "aibrain cli: the Status card says on, the CLI and its version, vision, and that no remark ran yet: " + status,
                    status == "On.  |  runs on: Claude Code CLI 2.1.292  |  vision: on  |  no remark yet this session");
                var at = new DateTime(2026, 10, 6, 14, 2, 0, DateTimeKind.Local);
                rig.Module.RecordRemark(at, 5200, null);
                ok &= Check(sb, "aibrain cli: ...and the last remark's time and duration once one has",
                    pane.Load()["brainStatus"].EndsWith("  |  last remark 14:02 (5.2 s)", StringComparison.Ordinal));
                rig.Module.RecordRemark(at, 800, "not signed in");
                ok &= Check(sb, "aibrain cli: ...and a failed one by its class, in the same plain voice",
                    pane.Load()["brainStatus"].EndsWith("  |  last ask 14:02 had no answer (not signed in)", StringComparison.Ordinal));
                rig.Save("brainRunsOn", "Local model", "useVision", "false");
                ok &= Check(sb, "WITNESS aibrain cli: on the local model the Status card names the local backend and its model",
                    pane.Load()["brainStatus"].StartsWith("On.  |  runs on: local OpenAI-compatible server gemma3:4b  |  vision: off", StringComparison.Ordinal));
                ok &= Check(sb, "aibrain cli: no log line and no Status line carries the account the card shows",
                    !rig.Host.LoggedLines.Exists(delegate(string l) { return l.Contains("someone@example.invalid"); }) &&
                    !pane.Load()["brainStatus"].Contains("@"));
            }
            return ok;
        }

        private static PaneAction FindAction(OptionsPane pane, string group, string label)
        {
            if (pane == null || pane.Actions == null) return null;
            foreach (PaneAction action in pane.Actions)
                if (action != null && action.Label == label && action.Group == group) return action;
            return null;
        }

        private static string PressPending(PaneAction action, string brainRunsOn)
        {
            if (action == null || action.InvokeWithPendingAsync == null) return null;
            var pending = new Dictionary<string, string>(StringComparer.Ordinal) { { "brainRunsOn", brainRunsOn } };
            Task<string> pressed = action.InvokeWithPendingAsync(pending);
            return pressed.Wait(TimeSpan.FromSeconds(20)) ? pressed.Result : null;
        }

        /// <summary>Validate and Update CLI act on the CLI on screen; the one-engine buttons refuse on the others; the
        /// audition runs through the module's runner.</summary>
        private static bool CheckCliPaneActions(StringBuilder sb)
        {
            bool ok = true;
            using (var rig = new CliRig("aibrain-cli-actions", ""))
            {
                OptionsPane pane = rig.Pane;
                PaneAction validate = FindAction(pane, AiBrainModule.CliCardGroup, "Validate");
                PaneAction update = FindAction(pane, AiBrainModule.CliCardGroup, "Update CLI");
                string validated = PressPending(validate, "Claude Code CLI");
                ok &= Check(sb, "aibrain cli: Validate makes one tiny call through the CLI chosen on screen, applied or not",
                    validated != null && validated.StartsWith("✓ Claude Code 2.1.292 answered in", StringComparison.Ordinal) &&
                    rig.Fake.Calls.FindAll(delegate(FakeCliCall c) { return c.IsModelCall; }).Count == 1 && validate.ReloadPaneAfter);
                ok &= Check(sb, "aibrain cli: Validate with no CLI on screen asks for one and runs nothing",
                    PressPending(validate, "Local model") == AiBrainModule.PickACliFirst &&
                    rig.Fake.Calls.FindAll(delegate(FakeCliCall c) { return c.IsModelCall; }).Count == 1);

                rig.Fake.Respond = delegate(FakeCliCall c, CancellationToken token)
                {
                    return c.IsModelCall
                        ? Task.FromResult(FakeCliProcess.Result(1, FakeCliProcess.CodexFailure(
                            "Your access token could not be refreshed because your refresh token was already used. Please log out and sign in again."), ""))
                        : FakeCliProcess.Answering("OK")(c, token);
                };
                string expired = PressPending(validate, "Codex CLI");
                ok &= Check(sb, "aibrain cli: Validate says in plain words that Codex's sign-in expired",
                    expired != null && expired.StartsWith("✗ Codex's sign-in has expired", StringComparison.Ordinal));

                var hold = new TaskCompletionSource<CliProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                rig.Fake.Respond = delegate(FakeCliCall c, CancellationToken token)
                {
                    return c.IsModelCall ? hold.Task : FakeCliProcess.Answering("OK")(c, token);
                };
                Task<CliAnswer> held = rig.Runner.AskAsync(new CliRequest { Agent = CodingAgentKind.Claude, Prompt = "p", Purpose = "remark" },
                    CancellationToken.None);
                string refused = PressPending(update, "Claude Code CLI");
                hold.SetResult(FakeCliProcess.Result(0, FakeCliProcess.ClaudeStream("done", false), ""));
                held.Wait(TimeSpan.FromSeconds(10));
                ok &= Check(sb, "aibrain cli: Update CLI refuses while a call of this module's is running",
                    refused != null && refused.StartsWith("⚠ Not now", StringComparison.Ordinal) &&
                    !rig.Fake.Calls.Exists(delegate(FakeCliCall c) { return c.Is("update"); }));
                rig.Fake.Respond = FakeCliProcess.Answering("OK");
                string updated = PressPending(update, "Claude Code CLI");
                ok &= Check(sb, "WITNESS aibrain cli: with nothing running, Update CLI runs the CLI's own update",
                    updated != null && updated.StartsWith("✓ Claude Code", StringComparison.Ordinal) &&
                    rig.Fake.Calls.Exists(delegate(FakeCliCall c) { return c.Is("update"); }));

                string onClaude = AiBrainModule.NotUsedWhileRunningOn("Claude Code CLI");
                ok &= Check(sb, "aibrain cli: Refresh local models, Test connection and Refresh cloud models refuse in plain words on a CLI",
                    onClaude == "✗ Not used while the brain runs on Claude Code CLI." &&
                    PressPending(FindAction(pane, "Local provider", "Refresh local models"), "Claude Code CLI") == onClaude &&
                    PressPending(FindAction(pane, "Cloud provider", "Test connection"), "Claude Code CLI") == onClaude &&
                    PressPending(FindAction(pane, "Cloud provider", "Refresh cloud models"), "Claude Code CLI") == onClaude);
                ok &= Check(sb, "aibrain cli: Refresh cloud models refuses on the local model, whose card it does not serve",
                    PressPending(FindAction(pane, "Cloud provider", "Refresh cloud models"), "Local model") ==
                        "✗ Not used while the brain runs on Local model.");
                ok &= Check(sb, "WITNESS aibrain cli: off a CLI the local- and cloud-slot buttons are not refused for a CLI",
                    rig.Module.CliRefusal(new Dictionary<string, string> { { "brainRunsOn", "Local model" } }) == null &&
                    rig.Module.CliRefusal(new Dictionary<string, string> { { "brainRunsOn", "Cloud provider" } }) == null);

                rig.Fake.Respond = FakeCliProcess.Answering(CliReply);
                rig.Fake.Clear();
                rig.Save("brainRunsOn", "Claude Code CLI", "cloudConsent", "false");
                string audition = rig.Module.PreviewDispositionAsync(false).GetAwaiter().GetResult();
                ok &= Check(sb, "aibrain cli: Show me 5 examples runs through the module's runner, with no consent switch to read",
                    audition != null && audition.Contains(" · 5 Claude Code CLI calls") && audition.Contains("A CLI REMARK") &&
                    rig.Fake.Calls.FindAll(delegate(FakeCliCall c) { return c.IsModelCall; }).Count == 5);
            }
            return ok;
        }
    }
}
