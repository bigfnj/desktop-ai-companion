using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DesktopAICompanion.Ai;
using DesktopAICompanion.CodingAgent;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.AiBrainModule
{
    /// <summary>
    /// Lane feature/cli-model-effort (aibrain 1.5.0, owner decision 2026-10-09): the model and the effort each coding-agent
    /// CLI runs on, chosen in the CLI card. The runner's own half (the flags, the checks, the model that answered) is in
    /// its self-check; this is the module's: the four settings and their defaults, the four rows and what they store, the
    /// Codex list read from the runner's cache, every call path (a remark, an audition, Validate) running on the choice,
    /// the CLI row and the Status card naming it, and a chosen Codex model that takes no images read the screen as text.
    /// No CLI is started (the runner's process seam is a fake), no model is called and no screen is captured: the two
    /// turns about the screen below are handed a zero-size monitor, which the capture refuses before it reads a pixel.
    /// </summary>
    public static partial class AiEngineProbe
    {
        internal static bool RunCliChoice(StringBuilder sb)
        {
            bool ok = true;
            ok &= GuardedCheck(sb, "CheckCliChoiceSettings", CheckCliChoiceSettings);
            ok &= GuardedCheck(sb, "CheckCliChoiceCalls", CheckCliChoiceCalls);
            ok &= GuardedCheck(sb, "CheckCliChoiceVision", CheckCliChoiceVision);
            ok &= GuardedCheck(sb, "CheckCliChoiceRows", CheckCliChoiceRows);
            ok &= GuardedCheck(sb, "CheckCliChoiceCodexList", CheckCliChoiceCodexList);
            ok &= GuardedCheck(sb, "CheckCliChoiceCardText", CheckCliChoiceCardText);
            ok &= GuardedCheck(sb, "CheckCliChoiceValidate", CheckCliChoiceValidate);
            return ok;
        }

        /// <summary>The CLI row's and the Status card's words for the module's default Claude Code choice, from the
        /// constants, so a default the coordinator's eval changes moves the expectation with it.</summary>
        private static string DefaultClaudeChoicePhrase()
        {
            return (AiSettings.DefaultClaudeModel.Length > 0 ? AiSettings.DefaultClaudeModel : "Claude Code's default model") +
                   " at " + AiSettings.DefaultClaudeEffort + " effort";
        }

        // A check that chooses a model or an effort to tell apart from the saved one takes it from these two, never as a
        // literal. When the eval set AI Brain's defaults to opus at medium (2026-10-09), four checks below had picked opus
        // or medium as the value that differs from the default, so each would pass with the choice ignored: the effort of a
        // remark on Claude Code's default, an Apply of "opus" (which changed nothing), the audition's on-screen effort and
        // Validate's on-screen model were all the saved values. An effort also avoids the runner's floor
        // (CodingAgentCli.DefaultEffort), which is what a call that dropped the effort would run at.

        /// <summary>The first of the runner's Claude Code aliases that is none of <paramref name="avoid"/>.</summary>
        private static string ClaudeModelOtherThan(params string[] avoid)
        {
            foreach (string alias in CodingAgentCli.ClaudeModelAliases)
                if (Array.IndexOf(avoid, alias) < 0) return alias;
            throw new InvalidOperationException("every Claude Code alias was excluded");
        }

        /// <summary>The first of the runner's efforts that is none of <paramref name="avoid"/>.</summary>
        private static string EffortOtherThan(params string[] avoid)
        {
            foreach (string effort in CodingAgentCli.Efforts)
                if (Array.IndexOf(avoid, effort) < 0) return effort;
            throw new InvalidOperationException("every effort was excluded");
        }

        /// <summary>One remark through the brain, or null when it threw: each check below is judged on its own label, so a
        /// call the runner refuses is THAT check's failure and not a throw that names the group instead.</summary>
        private static BrainResponse RemarkOrNull(AiBrain brain, string model)
        {
            try { return brain.GenerateWithRepeatGuardAsync(model, "text", null, CancellationToken.None).GetAwaiter().GetResult(); }
            catch { return null; }
        }

        /// <summary>The model call a fake saw last, or null.</summary>
        private static FakeCliCall LastModelCall(FakeCliProcess fake)
        {
            return fake.Calls.FindLast(delegate(FakeCliCall c) { return c.IsModelCall; });
        }

        private static AiSettings LoadedFrom(string root, string json)
        {
            File.WriteAllText(Path.Combine(root, "ai-settings.json"),
                "{ \"SchemaVersion\": " + AiSettings.CurrentSchemaVersion + json + " }", new UTF8Encoding(false));
            return AiSettings.Load();
        }

        /// <summary>The four settings: an existing file moves to the defaults, a blank model is a choice and a blank effort
        /// is not, case and spacing are read through, and a value this version does not offer is kept.</summary>
        private static bool CheckCliChoiceSettings(StringBuilder sb)
        {
            bool ok = true;
            string previousRoot = AiPaths.CurrentRootForDiagnostics;
            string root = Path.Combine(Path.GetTempPath(), "dp-aibrain-cli-choice-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                AiPaths.SwapRoot(root);
                AiSettings old = LoadedFrom(root, ", \"AiBrainEnabled\": true, \"CliBackend\": \"claude\"");
                ok &= Check(sb, "aibrain cli model: a settings file written before 1.5.0 has no model or effort and runs on the module's defaults",
                    old.CliClaudeModel == AiSettings.DefaultClaudeModel && old.CliClaudeEffort == AiSettings.DefaultClaudeEffort &&
                    old.CliCodexModel == AiSettings.DefaultCodexModel && old.CliCodexEffort == AiSettings.DefaultCodexEffort);
                AiSettings blank = LoadedFrom(root,
                    ", \"CliClaudeModel\": \"\", \"CliClaudeEffort\": \"\", \"CliCodexModel\": \"\", \"CliCodexEffort\": \"\"");
                ok &= Check(sb, "aibrain cli model: a blank model is kept (Claude Code's default, Codex's automatic pick) and a blank effort is the default",
                    blank.CliClaudeModel == "" && blank.CliCodexModel == "" &&
                    blank.CliClaudeEffort == AiSettings.DefaultClaudeEffort && blank.CliCodexEffort == AiSettings.DefaultCodexEffort);
                AiSettings spaced = LoadedFrom(root,
                    ", \"CliClaudeModel\": \" Sonnet \", \"CliClaudeEffort\": \"HIGH\", \"CliCodexModel\": \"GPT-Selftest-1\", \"CliCodexEffort\": \" Medium\"");
                ok &= Check(sb, "aibrain cli model: a stored model and effort are read whatever their case and spacing",
                    spaced.CliClaudeModel == "sonnet" && spaced.CliClaudeEffort == "high" && spaced.CliCodexModel == "gpt-selftest-1" &&
                    spaced.CliCodexEffort == "medium");
                AiSettings unoffered = LoadedFrom(root, ", \"CliClaudeModel\": \"claude-opus-5-5\", \"CliClaudeEffort\": \"xhigh\"");
                ok &= Check(sb, "aibrain cli model: a model or effort this version does not offer is kept, for the runner to refuse and the pane to show",
                    unoffered.CliClaudeModel == "claude-opus-5-5" && unoffered.CliClaudeEffort == "xhigh");
                // Over 96 characters, cut and ended in "…" (review finding F4), the runner's reader Remembrance shares: a
                // value padded past the cut with spaces is refused as it stands, where a plain cut, then trimmed by the
                // runner, ran the call on the word its padding began with (a text-only slug here, so with Use vision on
                // every remark ended in "takes no images" instead of the OCR turn).
                AiSettings padded = LoadedFrom(root,
                    ", \"CliCodexModel\": \"text-only-low" + new string(' ', 100) + "junk\", \"CliClaudeModel\": \"" + new string('a', 200) + "\"");
                string passedModel, passedEffort;
                ok &= Check(sb, "aibrain cli model: a Codex model padded past 96 characters is refused as it stands, never passed on as the slug its padding began with",
                    padded.CliCodexModel.StartsWith("text-only-low ", StringComparison.Ordinal) && padded.CliCodexModel.EndsWith("…", StringComparison.Ordinal) &&
                    CodingAgentCli.CheckChoice(CodingAgentKind.Codex, padded.CliCodexModel, "low", out passedModel, out passedEffort) != null);
                ok &= Check(sb, "aibrain cli model: a stored value over 96 characters is cut and ended in an ellipsis, which the runner refuses",
                    padded.CliClaudeModel == new string('a', 95) + "…" &&
                    CodingAgentCli.CheckChoice(CodingAgentKind.Claude, padded.CliClaudeModel, "low", out passedModel, out passedEffort) != null);
                // Control and bidirectional characters dropped, a line separator a space (review finding F3), so what the
                // dropdown, the Status card and a refusal show is the value that is checked.
                AiSettings hostile = LoadedFrom(root,
                    ", \"CliClaudeModel\": \"son\\tnet\\n\", \"CliClaudeEffort\": \"hi\\u202Egh\", \"CliCodexModel\": \"vision\\u2028later\"");
                ok &= Check(sb, "aibrain cli model: a stored value is read with its control and bidi characters dropped and a line separator as a space",
                    hostile.CliClaudeModel == "sonnet" && hostile.CliClaudeEffort == "high" && hostile.CliCodexModel == "vision later");
            }
            finally
            {
                AiPaths.SwapRoot(previousRoot);
                try { Directory.Delete(root, true); } catch { }
            }
            var aliases = new List<string>(CodingAgentCli.ClaudeModelAliases);
            aliases.Add("");
            ok &= Check(sb, "aibrain cli model: the rows offer exactly the runner's aliases and Claude Code's default, and its efforts",
                string.Join("|", AiBrainModule.ClaudeModelValuesForDiagnostics()) == string.Join("|", aliases) &&
                string.Join("|", AiBrainModule.EffortValuesForDiagnostics()) == string.Join("|", CodingAgentCli.Efforts));
            return ok;
        }

        /// <summary>Every call path runs on the settings' choice: a remark (never on the id the brain's model policy hands
        /// the backend), the module's defaults, Codex's slug and effort, an Apply that changes only the model, and the
        /// persona audition on what is on screen.</summary>
        private static bool CheckCliChoiceCalls(StringBuilder sb)
        {
            bool ok = true;
            using (var scratch = new FakeCliScratch())
            {
                var fake = new FakeCliProcess { Respond = FakeCliProcess.Answering(CliReply) };
                CodingAgentCli runner = scratch.NewRunner(fake, new List<string>());
                var chosen = new AiSettings { CliBackend = "claude", CliClaudeModel = "sonnet", CliClaudeEffort = "high" };
                using (AiBrain brain = AiBrainModule.CreateBrain(chosen, null, null, runner))
                {
                    BrainResponse said = RemarkOrNull(brain, "a-slot-model-the-cli-never-takes");
                    FakeCliCall call = LastModelCall(fake);
                    ok &= Check(sb, "aibrain cli model: a Claude Code remark runs on the model and effort the settings chose, never the brain's own model id",
                        said != null && call != null && call.After("--model") == "sonnet" && call.After("--effort") == "high");
                }

                fake.Clear();
                string chosenEffort = EffortOtherThan(AiSettings.DefaultClaudeEffort, CodingAgentCli.DefaultEffort);
                using (AiBrain brain = AiBrainModule.CreateBrain(
                    new AiSettings { CliBackend = "claude", CliClaudeModel = "", CliClaudeEffort = chosenEffort }, null, null, runner))
                {
                    BrainResponse said = RemarkOrNull(brain, "claude-code-cli");
                    FakeCliCall call = LastModelCall(fake);
                    ok &= Check(sb, "aibrain cli model: Claude Code's default sends no --model, at the effort chosen",
                        said != null && call != null && !call.Arguments.Contains("--model") && call.After("--effort") == chosenEffort);
                }

                fake.Clear();
                using (AiBrain brain = AiBrainModule.CreateBrain(new AiSettings { CliBackend = "claude" }, null, null, runner))
                {
                    BrainResponse said = RemarkOrNull(brain, "claude-code-cli");
                    FakeCliCall call = LastModelCall(fake);
                    ok &= Check(sb, "aibrain cli model: on the module's defaults a Claude Code remark runs on the default model and effort",
                        said != null && call != null && call.After("--effort") == AiSettings.DefaultClaudeEffort &&
                        (AiSettings.DefaultClaudeModel.Length > 0
                            ? call.After("--model") == AiSettings.DefaultClaudeModel
                            : !call.Arguments.Contains("--model")));
                }

                fake.Clear();
                using (AiBrain brain = AiBrainModule.CreateBrain(
                    new AiSettings { CliBackend = "codex", CliCodexModel = "vision-later", CliCodexEffort = "high" }, null, null, runner))
                {
                    BrainResponse said = RemarkOrNull(brain, "codex-cli");
                    FakeCliCall call = LastModelCall(fake);
                    ok &= Check(sb, "aibrain cli model: a Codex remark runs on the slug and effort the settings chose",
                        said != null && call != null && call.IsCodex && call.After("-m") == "vision-later" && call.Arguments.Contains("model_reasoning_effort=high"));
                }
            }

            // Through the module: an Apply that changes only the model, and the audition of what is on screen.
            using (var rig = new CliRig("aibrain-cli-choice-calls", ", \"CliBackend\": \"claude\""))
            {
                rig.Fake.Respond = FakeCliProcess.Answering(CliReply);
                SpinWait.SpinUntil(delegate { return rig.Module.SessionForDiagnostics.LiveBrainForDiagnostics != null; }, TimeSpan.FromSeconds(5));
                AiBrain before = rig.Module.SessionForDiagnostics.LiveBrainForDiagnostics;
                // Not the default, so the rebuilt brain is on it only if the Apply stored it; not the audition's below.
                string applied = ClaudeModelOtherThan(AiSettings.DefaultClaudeModel, "sonnet");
                bool saved = rig.Save("brainRunsOn", "Claude Code CLI", "cliClaudeModel", AiBrainModule.ClaudeModelLabel(applied));
                CodingAgentBackend rebuilt = null;
                SpinWait.SpinUntil(delegate
                {
                    AiBrain now = rig.Module.SessionForDiagnostics.LiveBrainForDiagnostics;
                    rebuilt = now != null && !ReferenceEquals(now, before) ? now.BackendForDiagnostics as CodingAgentBackend : null;
                    return rebuilt != null;
                }, TimeSpan.FromSeconds(5));
                ok &= Check(sb, "aibrain cli model: an Apply that changes only the model rebuilds the live brain on it (" +
                    (rebuilt == null ? "no new brain" : rebuilt.ModelForDiagnostics + " at " + rebuilt.EffortForDiagnostics) + ")",
                    saved && rebuilt != null && rebuilt.ModelForDiagnostics == applied && rebuilt.EffortForDiagnostics == rig.Settings.CliClaudeEffort);

                rig.Fake.Clear();
                string auditionEffort = EffortOtherThan(rig.Settings.CliClaudeEffort, CodingAgentCli.DefaultEffort);
                var pending = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { "brainRunsOn", "Claude Code CLI" }, { "cliClaudeModel", AiBrainModule.ClaudeModelLabel("sonnet") },
                    { "cliClaudeEffort", AiBrainModule.EffortLabel(auditionEffort) },
                };
                string audition = rig.Module.PreviewDispositionAsync(false, pending).GetAwaiter().GetResult();
                List<FakeCliCall> samples = rig.Fake.Calls.FindAll(delegate(FakeCliCall c) { return c.IsModelCall; });
                ok &= Check(sb, "aibrain cli model: the persona audition runs on the model and effort on screen, applied or not",
                    audition != null && audition.Contains("A CLI REMARK") && samples.Count == 5 &&
                    samples.TrueForAll(delegate(FakeCliCall c) { return c.After("--model") == "sonnet" && c.After("--effort") == auditionEffort; }) &&
                    rig.Settings.CliClaudeModel == applied);
                // Review finding F16: the header named "claude-code-cli", the brain's own id, where the model goes, so two
                // auditions on two models read alike.
                string head = audition == null ? "(no audition)" : audition.Split('\n')[0];
                ok &= Check(sb, "aibrain cli model: the persona audition's header names the CLI and the model and effort on screen, never the brain's own id: " + head,
                    head.EndsWith(" — as shown in the pane, not yet applied · Claude Code CLI, sonnet at " + auditionEffort +
                                  " effort · made-up scenes · 5 calls", StringComparison.Ordinal) &&
                    !audition.Contains("claude-code-cli"));
            }
            return ok;
        }

        /// <summary>A zero-size monitor: the capture refuses it before it reads a pixel, after every decision a turn makes
        /// first, so a check can read those decisions from the log with no screen captured.</summary>
        private static ScreenContext NoScreen()
        {
            return new ScreenContext
            {
                MonitorBounds = new PixelRect(0, 0, 0, 0),
                ForegroundWindowBounds = new PixelRect(0, 0, 0, 0),
                WindowTitle = "",
                ProcessName = "",
            };
        }

        /// <summary>With Use vision on, a chosen Codex model whose catalog says it takes no images is read the screen as
        /// text, decided before the capture on the hotkey's turn and the live audition alike; every other model still
        /// gets the screenshot.</summary>
        private static bool CheckCliChoiceVision(StringBuilder sb)
        {
            bool ok = true;
            Action<string> previousSink = AiBrain.LogSink;
            var lines = new List<string>();
            try
            {
                AiBrain.LogSink = delegate(string line) { lock (lines) lines.Add(line); };
                using (var scratch = new FakeCliScratch())
                {
                    var fake = new FakeCliProcess { Respond = FakeCliProcess.Answering(CliReply) };
                    CodingAgentCli runner = scratch.NewRunner(fake, new List<string>());
                    // The catalog cached, as a first Codex call or a pane's details leave it.
                    runner.RefreshDetailsAsync(CodingAgentKind.Codex, CancellationToken.None).GetAwaiter().GetResult();
                    Func<string, string, bool> sendsScreenshot = delegate(string cli, string slug)
                    {
                        using (AiBrain brain = AiBrainModule.CreateBrain(
                            new AiSettings { CliBackend = cli, CliCodexModel = slug, UseVision = true }, null, null, runner))
                            return brain.SendsScreenshot(true);
                    };
                    ok &= Check(sb, "aibrain cli model: with Use vision on, a chosen Codex model its catalog says takes no images reads the screen as text",
                        !sendsScreenshot("codex", "text-only-low"));
                    ok &= Check(sb, "WITNESS aibrain cli model: ...while one that takes images, Automatic, a slug the catalog does not list and Claude Code still send the screenshot",
                        sendsScreenshot("codex", "vision-second") && sendsScreenshot("codex", "") && sendsScreenshot("codex", "not-listed-selftest-1") &&
                        sendsScreenshot("claude", "text-only-low"));
                    // A slug with spaces round it, as a hand-edited file can hold (review finding F4): the backend trims it as
                    // the runner's CheckChoice does, so the pre-capture check looks up the slug that will be passed.
                    using (var padded = new CodingAgentBackend(runner, CodingAgentKind.Codex, TimeSpan.FromSeconds(5), "  text-only-low \t", "low"))
                        ok &= Check(sb, "aibrain cli model: a chosen Codex slug with spaces round it is looked up trimmed, so the check before the capture still finds it takes no images",
                            padded.ChosenModelTakesNoImages() && padded.ModelForDiagnostics == "text-only-low");

                    fake.Clear();
                    foreach (string slug in new[] { "text-only-low", "vision-second" })
                        using (AiBrain brain = AiBrainModule.CreateBrain(
                            new AiSettings { CliBackend = "codex", CliCodexModel = slug, UseVision = true }, null, null, runner))
                        {
                            lock (lines) lines.Clear();
                            brain.AskAboutScreenAsync(NoScreen(), null, true, CancellationToken.None).GetAwaiter().GetResult();
                            string resolved;
                            lock (lines) resolved = lines.Find(delegate(string l) { return l.StartsWith("model resolve: ", StringComparison.Ordinal); });
                            bool blind = slug == "text-only-low";
                            ok &= Check(sb, (blind
                                    ? "aibrain cli model: the hotkey's turn on a Codex model that takes no images decides the text path before its capture: "
                                    : "WITNESS aibrain cli model: the hotkey's turn on a Codex model that takes images takes the screenshot path: ") +
                                    slug + ", " + (resolved ?? "(no resolve line)"),
                                resolved != null && resolved.EndsWith(blind ? " vision=False" : " vision=True", StringComparison.Ordinal));

                            lock (lines) lines.Clear();
                            brain.SampleDispositionAsync("samuel", NoScreen(), null, TimeSpan.FromSeconds(5), CancellationToken.None)
                                .GetAwaiter().GetResult();
                            string audition;
                            lock (lines) audition = lines.Find(delegate(string l) { return l.StartsWith("persona audition: ", StringComparison.Ordinal); });
                            ok &= Check(sb, (blind
                                    ? "aibrain cli model: the live audition on a Codex model that takes no images reads the screen as text: "
                                    : "WITNESS aibrain cli model: the live audition on a Codex model that takes images reads it as a picture: ") +
                                    slug + ", " + (audition ?? "(no audition line)"),
                                audition != null && audition.Contains(blind ? " source=live-ocr " : " source=live-vision "));
                        }
                    ok &= Check(sb, "aibrain cli model: neither turn about a zero-size screen reached the CLI",
                        !fake.Calls.Exists(delegate(FakeCliCall c) { return c.IsModelCall; }));
                }
            }
            finally { AiBrain.LogSink = previousSink; }
            return ok;
        }

        /// <summary>The four rows: where they sit, what greys them, what they offer, what Load shows and Apply stores, and
        /// that a row handed back as no option changes nothing.</summary>
        private static bool CheckCliChoiceRows(StringBuilder sb)
        {
            bool ok = true;
            using (var rig = new CliRig("aibrain-cli-choice-rows", ""))
            {
                OptionsPane pane = rig.Pane;
                SettingField claudeModel = FieldOf(pane, "cliClaudeModel");
                SettingField claudeEffort = FieldOf(pane, "cliClaudeEffort");
                SettingField codexModel = FieldOf(pane, "cliCodexModel");
                SettingField codexEffort = FieldOf(pane, "cliCodexEffort");
                ok &= Check(sb, "aibrain cli model: the CLI card has Claude Code model and effort dropdowns, live only while Claude Code is the CLI on screen",
                    claudeModel != null && claudeModel.Kind == SettingKind.Enum && claudeModel.Label == "Claude Code model" &&
                    claudeModel.Group == AiBrainModule.CliCardGroup && claudeModel.EnabledWhen == AiBrainModule.OnClaudeCliOnly &&
                    claudeEffort != null && claudeEffort.Kind == SettingKind.Enum && claudeEffort.Label == "Claude Code effort" &&
                    claudeEffort.Group == AiBrainModule.CliCardGroup && claudeEffort.EnabledWhen == AiBrainModule.OnClaudeCliOnly);
                ok &= Check(sb, "aibrain cli model: ...and Codex model and effort dropdowns, live only while Codex is",
                    codexModel != null && codexModel.Kind == SettingKind.Enum && codexModel.Label == "Codex model" &&
                    codexModel.Group == AiBrainModule.CliCardGroup && codexModel.EnabledWhen == "brainRunsOn=" + CodingAgents.ChoiceLabel(CodingAgentKind.Codex) &&
                    codexEffort != null && codexEffort.Kind == SettingKind.Enum && codexEffort.Label == "Codex effort" &&
                    codexEffort.Group == AiBrainModule.CliCardGroup && codexEffort.EnabledWhen == codexModel.EnabledWhen);
                ok &= Check(sb, "aibrain cli model: the four rows sit after the sign-in token and before Status",
                    IndexOfField(pane, "cliToken") < IndexOfField(pane, "cliClaudeModel") &&
                    IndexOfField(pane, "cliClaudeModel") < IndexOfField(pane, "cliClaudeEffort") &&
                    IndexOfField(pane, "cliClaudeEffort") < IndexOfField(pane, "cliCodexModel") &&
                    IndexOfField(pane, "cliCodexModel") < IndexOfField(pane, "cliCodexEffort") &&
                    IndexOfField(pane, "cliCodexEffort") < IndexOfField(pane, "cliStatus"));

                IReadOnlyDictionary<string, string> shown = pane.Load();
                // Re-pointed in round 2: the plain names (the owner, 2026-10-09), where the first build said what Haiku and Opus
                // cost and the editor column cut both off.
                ok &= Check(sb, "aibrain cli model: the Claude Code model offers Haiku, Sonnet, Opus and Claude Code's default, and each effort Low, Medium and High: " +
                    string.Join(" / ", claudeModel == null ? new string[0] : claudeModel.Options),
                    claudeModel != null && string.Join("|", claudeModel.Options) == "Haiku|Sonnet|Opus|Claude Code's default" &&
                    string.Join("|", claudeEffort.Options) == "Low|Medium|High" && string.Join("|", codexEffort.Options) == "Low|Medium|High");
                ok &= Check(sb, "aibrain cli model: Load shows the saved choice by its label, the module's defaults on a new install",
                    shown["cliClaudeModel"] == AiBrainModule.ClaudeModelLabel(AiSettings.DefaultClaudeModel) &&
                    shown["cliClaudeEffort"] == AiBrainModule.EffortLabel(AiSettings.DefaultClaudeEffort) &&
                    shown["cliCodexModel"] == (AiSettings.DefaultCodexModel.Length == 0 ? AiBrainModule.CodexAutomaticLabel : AiSettings.DefaultCodexModel) &&
                    shown["cliCodexEffort"] == AiBrainModule.EffortLabel(AiSettings.DefaultCodexEffort) &&
                    Array.IndexOf(claudeModel.Options, shown["cliClaudeModel"]) >= 0 && Array.IndexOf(codexModel.Options, shown["cliCodexModel"]) >= 0);

                bool applied = rig.Save("cliClaudeModel", "Sonnet", "cliClaudeEffort", "High", "cliCodexEffort", "Medium");
                AiSettings onDisk = AiSettings.Load();
                ok &= Check(sb, "aibrain cli model: Apply stores the alias and the effort for the labels chosen",
                    applied && rig.Settings.CliClaudeModel == "sonnet" && rig.Settings.CliClaudeEffort == "high" &&
                    rig.Settings.CliCodexEffort == "medium" && onDisk.CliClaudeModel == "sonnet" && onDisk.CliClaudeEffort == "high" &&
                    pane.Load()["cliClaudeModel"] == "Sonnet");
                rig.Save("cliClaudeModel", "", "cliClaudeEffort", "", "cliCodexModel", "", "cliCodexEffort", "not an option");
                ok &= Check(sb, "aibrain cli model: a row handed back as no option's text changes nothing, and never becomes Claude Code's default",
                    rig.Settings.CliClaudeModel == "sonnet" && rig.Settings.CliClaudeEffort == "high" &&
                    rig.Settings.CliCodexModel == AiSettings.DefaultCodexModel && rig.Settings.CliCodexEffort == "medium");
                rig.Save("cliClaudeModel", "Claude Code's default");
                ok &= Check(sb, "WITNESS aibrain cli model: choosing Claude Code's default stores no model", rig.Settings.CliClaudeModel == "" &&
                    AiSettings.Load().CliClaudeModel == "" && pane.Load()["cliClaudeModel"] == "Claude Code's default");
                // Round 2: a value handed back under a label the first 1.5.0 build showed still stores its alias, and loads
                // under the plain name.
                rig.Save("cliClaudeModel", "Opus (most capable, heaviest on usage)");
                bool formerOpus = rig.Settings.CliClaudeModel == "opus" && pane.Load()["cliClaudeModel"] == "Opus";
                rig.Save("cliClaudeModel", "Haiku (fastest, lightest on usage)");
                ok &= Check(sb, "aibrain cli model: a value handed back under the first build's longer label still stores its alias",
                    formerOpus && rig.Settings.CliClaudeModel == "haiku" && pane.Load()["cliClaudeModel"] == "Haiku");
            }

            using (var rig = new CliRig("aibrain-cli-choice-unoffered",
                ", \"CliClaudeModel\": \"claude-opus-5-5\", \"CliClaudeEffort\": \"xhigh\", \"CliCodexModel\": \"not-listed-selftest-1\""))
            {
                OptionsPane pane = rig.Pane;
                IReadOnlyDictionary<string, string> shown = pane.Load();
                SettingField claudeModel = FieldOf(pane, "cliClaudeModel");
                SettingField claudeEffort = FieldOf(pane, "cliClaudeEffort");
                SettingField codexModel = FieldOf(pane, "cliCodexModel");
                ok &= Check(sb, "aibrain cli model: a saved value the dropdown does not offer is one of its options and loads as itself",
                    shown["cliClaudeModel"] == "claude-opus-5-5" && Array.IndexOf(claudeModel.Options, "claude-opus-5-5") >= 0 &&
                    shown["cliClaudeEffort"] == "xhigh" && Array.IndexOf(claudeEffort.Options, "xhigh") >= 0);
                ok &= Check(sb, "aibrain cli model: ...a Codex slug the cached list does not hold too",
                    shown["cliCodexModel"] == "not-listed-selftest-1" && Array.IndexOf(codexModel.Options, "not-listed-selftest-1") >= 0);
                var handedBack = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (string id in new[] { "cliClaudeModel", "cliClaudeEffort", "cliCodexModel", "cliCodexEffort" }) handedBack[id] = shown[id];
                bool saved = pane.Save(handedBack);
                ok &= Check(sb, "aibrain cli model: ...so an Apply that hands it back keeps it",
                    saved && rig.Settings.CliClaudeModel == "claude-opus-5-5" && rig.Settings.CliClaudeEffort == "xhigh" &&
                    rig.Settings.CliCodexModel == "not-listed-selftest-1");
            }
            return ok;
        }

        /// <summary>The Codex dropdown's options for these listed labels, as a fresh install shows them: the module's
        /// default slug, when it is one, sits second as itself unless the list holds it.</summary>
        private static string ExpectedCodexOptions(params string[] listed)
        {
            var options = new List<string> { AiBrainModule.CodexAutomaticLabel };
            options.AddRange(listed);
            string slug = AiSettings.DefaultCodexModel;
            bool isListed = slug == "text-only-low" || slug == "vision-second" || slug == "vision-later";
            if (slug.Length > 0 && !(isListed && listed.Length > 0)) options.Insert(1, slug);
            return string.Join("|", options);
        }

        /// <summary>The Codex model dropdown: Automatic alone until the catalog is cached, read from the runner's cache so a
        /// pane open starts no Codex; then the listed models by name; a name chosen stores its slug.</summary>
        private static bool CheckCliChoiceCodexList(StringBuilder sb)
        {
            bool ok = true;
            using (var rig = new CliRig("aibrain-cli-choice-codex", ", \"CliBackend\": \"claude\""))
            {
                OptionsPane pane = rig.Pane;
                SettingField codexModel = FieldOf(pane, "cliCodexModel");
                rig.Fake.Clear();
                pane.Load();
                bool startedCodex = SpinWait.SpinUntil(delegate { return rig.Fake.Calls.Exists(delegate(FakeCliCall c) { return c.IsCodex; }); },
                    TimeSpan.FromSeconds(1.5));
                ok &= Check(sb, "aibrain cli model: before Codex's catalog is cached the Codex model dropdown offers Automatic alone, and a pane open starts no Codex: " +
                    string.Join(" / ", codexModel == null ? new string[0] : codexModel.Options),
                    codexModel != null && string.Join("|", codexModel.Options) == ExpectedCodexOptions() && !startedCodex);

                rig.Runner.RefreshDetailsAsync(CodingAgentKind.Codex, CancellationToken.None).GetAwaiter().GetResult();
                pane.Load();
                // Re-pointed in round 2: the catalog's plain names, with no note in any label (the owner, 2026-10-09: "same for
                // codex, simple and short is fine"); text-only-low's label said "(takes no images)" until then.
                ok &= Check(sb, "aibrain cli model: once the catalog is cached it offers Automatic and the listed models by their plain names, no note in any label: " +
                    string.Join(" / ", codexModel.Options),
                    string.Join("|", codexModel.Options) == ExpectedCodexOptions("Text Only", "Vision Second", "Vision Later"));

                bool saved = rig.Save("brainRunsOn", "Codex CLI", "cliCodexModel", "Vision Second");
                ok &= Check(sb, "aibrain cli model: choosing a Codex model by name stores its slug",
                    saved && rig.Settings.CliCodexModel == "vision-second" && AiSettings.Load().CliCodexModel == "vision-second" &&
                    pane.Load()["cliCodexModel"] == "Vision Second");
                rig.Save("cliCodexModel", "Text Only" + AiBrainModule.FormerCodexNoImagesNote);
                ok &= Check(sb, "aibrain cli model: a Codex model handed back under the first build's \"(takes no images)\" label still stores its slug",
                    rig.Settings.CliCodexModel == "text-only-low" && pane.Load()["cliCodexModel"] == "Text Only");
                rig.Save("cliCodexModel", AiBrainModule.CodexAutomaticLabel);
                ok &= Check(sb, "WITNESS aibrain cli model: ...and Automatic stores none, the runner's automatic pick",
                    rig.Settings.CliCodexModel == "" && pane.Load()["cliCodexModel"] == AiBrainModule.CodexAutomaticLabel);
            }
            return ok;
        }

        /// <summary>The CLI row and the Status card name the choice, and what last answered once something has; a chosen
        /// Codex model that takes no images says what Use vision does with it.</summary>
        private static bool CheckCliChoiceCardText(StringBuilder sb)
        {
            bool ok = true;
            using (var rig = new CliRig("aibrain-cli-choice-text", ""))
            {
                OptionsPane pane = rig.Pane;
                rig.Runner.RefreshDetailsAsync(CodingAgentKind.Claude, CancellationToken.None).GetAwaiter().GetResult();
                rig.Save("brainRunsOn", "Claude Code CLI", "cliClaudeModel", "Sonnet", "cliClaudeEffort", "High", "useVision", "false");
                IReadOnlyDictionary<string, string> shown = pane.Load();
                ok &= Check(sb, "aibrain cli model: the CLI row names the Claude Code model and effort chosen: " + shown["cliName"],
                    shown["cliName"] == "Claude Code 2.1.292, sonnet at high effort");
                ok &= Check(sb, "aibrain cli model: the Status card names the Claude Code model and effort chosen: " + shown["brainStatus"],
                    shown["brainStatus"].StartsWith("On.  |  runs on: Claude Code CLI 2.1.292, sonnet at high effort  |  vision: off", StringComparison.Ordinal));
                PaneAction validate = FindAction(pane, AiBrainModule.CliCardGroup, "Validate");
                PressWith(validate, "brainRunsOn", "Claude Code CLI");
                shown = pane.Load();
                string answered = FakeCliProcess.AnsweredModelFor("sonnet");
                ok &= Check(sb, "aibrain cli model: once a call has answered, the CLI row names the model that answered: " + shown["cliName"],
                    shown["cliName"] == "Claude Code 2.1.292, sonnet at high effort; last answered on " + answered + " at high effort");
                ok &= Check(sb, "aibrain cli model: once a call has answered, the Status card names the model that answered: " + shown["brainStatus"],
                    shown["brainStatus"].StartsWith("On.  |  runs on: Claude Code CLI 2.1.292, sonnet at high effort, last answered on " + answered + "  |  ",
                        StringComparison.Ordinal));
                rig.Save("cliClaudeModel", "Claude Code's default", "cliClaudeEffort", "Medium");
                ok &= Check(sb, "aibrain cli model: on Claude Code's default the CLI row says so, at the effort chosen: " + pane.Load()["cliName"],
                    pane.Load()["cliName"].StartsWith("Claude Code 2.1.292, Claude Code's default model at medium effort; last answered on ", StringComparison.Ordinal));
                // Review finding F13: the answer kept is the Validate's, on sonnet at high, and the choice saved since is
                // another, so both rows say what that call asked for; the two exact checks above, where the call asked for
                // the saved choice, are the witness that the note is said only then.
                shown = pane.Load();
                string askedNote = " (that call asked for sonnet at high effort)";
                ok &= Check(sb, "aibrain cli model: after the choice changed, the CLI row says what the call that last answered asked for: " + shown["cliName"],
                    shown["cliName"] == "Claude Code 2.1.292, Claude Code's default model at medium effort; last answered on " + answered + askedNote);
                ok &= Check(sb, "aibrain cli model: ...and so does the Status card: " + shown["brainStatus"],
                    shown["brainStatus"].StartsWith("On.  |  runs on: Claude Code CLI 2.1.292, Claude Code's default model at medium effort, last answered on " +
                                                    answered + askedNote + "  |  ", StringComparison.Ordinal));

                // Review finding F10: the Status card's short form is the CLI row's, without the effort, so Claude Code's
                // own fallback is said in both rows; this card's short form had dropped it.
                Func<FakeCliCall, CancellationToken, Task<CliProcessResult>> answering = rig.Fake.Respond;
                rig.Fake.Respond = delegate(FakeCliCall c, CancellationToken token)
                {
                    if (!c.IsModelCall || c.IsCodex) return answering(c, token);
                    return Task.FromResult(FakeCliProcess.Result(0,
                        "{\"type\":\"system\",\"subtype\":\"model_fallback\",\"original_model\":\"claude-opus-selftest-9\",\"fallback_model\":\"claude-sonnet-selftest-9\"}\n" +
                        FakeCliProcess.ClaudeStream(CliReply, false, "claude-sonnet-selftest-9", "fake-default"), ""));
                };
                PressWith(validate, "brainRunsOn", "Claude Code CLI");
                rig.Fake.Respond = answering;
                shown = pane.Load();
                string fellBack = "on claude-sonnet-selftest-9 (Claude Code fell back to it from claude-opus-selftest-9)";
                ok &= Check(sb, "aibrain cli model: Claude Code's fallback is said in the CLI row and the Status card alike: " + shown["brainStatus"],
                    shown["cliName"] == "Claude Code 2.1.292, Claude Code's default model at medium effort; last answered " + fellBack + " at medium effort" &&
                    shown["brainStatus"].StartsWith("On.  |  runs on: Claude Code CLI 2.1.292, Claude Code's default model at medium effort, last answered " +
                                                    fellBack + "  |  ", StringComparison.Ordinal));

                CodingAgentCli.CliDetails codex = rig.Runner.RefreshDetailsAsync(CodingAgentKind.Codex, CancellationToken.None).GetAwaiter().GetResult();
                pane.Load();   // the dropdown the user would see once the catalog is cached, so its names are options
                string named = "Codex " + codex.Version;
                rig.Save("brainRunsOn", "Codex CLI", "cliCodexModel", "Text Only", "cliCodexEffort", "Low", "useVision", "true");
                shown = pane.Load();
                ok &= Check(sb, "aibrain cli model: the CLI row names a chosen Codex model, and with Use vision on that one which takes no images reads the screen as text: " +
                    shown["cliName"],
                    rig.Settings.CliCodexModel == "text-only-low" &&
                    shown["cliName"] == named + ", text-only-low at low effort (it takes no images, so with Use vision on its remarks read the screen as text)");
                // Review finding F15: Goes through it said a screenshot goes while every remark sent the OCR text.
                ok &= Check(sb, "aibrain cli model: with Use vision on, Goes through it says a chosen Codex model that takes no images is sent the screen's text: " +
                    shown["cliSends"],
                    shown["cliSends"].Contains("Use vision is on, but the Codex model chosen takes no images, so Ask, the hotkey, the tray row, " +
                                               "the random drops and the poke reaction each send the text read off the screen (OCR) instead of a screenshot.") &&
                    !shown["cliSends"].Contains("a screenshot of the window"));
                rig.Save("cliCodexModel", "Vision Second");
                ok &= Check(sb, "WITNESS aibrain cli model: ...and says nothing of images for one that takes them: " + pane.Load()["cliName"],
                    pane.Load()["cliName"] == named + ", vision-second at low effort");
                ok &= Check(sb, "WITNESS aibrain cli model: ...and Goes through it says a screenshot goes to one that takes them",
                    pane.Load()["cliSends"].Contains("each send a screenshot of the window (Use vision is on)") &&
                    !pane.Load()["cliSends"].Contains("takes no images"));
                rig.Save("cliCodexModel", AiBrainModule.CodexAutomaticLabel, "useVision", "false");
                ok &= Check(sb, "aibrain cli model: on Automatic the CLI row names Codex's pick and the effort: " + pane.Load()["cliName"],
                    pane.Load()["cliName"] == named + ", text-only-low, the first model this Codex lists, at low effort");
                ok &= Check(sb, "aibrain cli model: ...and the Status card says it runs on the automatic pick: " + pane.Load()["brainStatus"],
                    pane.Load()["brainStatus"].StartsWith("On.  |  runs on: Codex CLI " + codex.Version + ", its automatic pick at low effort", StringComparison.Ordinal));
            }
            return ok;
        }

        /// <summary>Validate tests the model and effort on screen, applied or not, names the model that answered, and saves
        /// neither; with no model row on screen it tests the saved choice.</summary>
        private static bool CheckCliChoiceValidate(StringBuilder sb)
        {
            bool ok = true;
            using (var rig = new CliRig("aibrain-cli-choice-validate", ""))
            {
                OptionsPane pane = rig.Pane;
                PaneAction validate = FindAction(pane, AiBrainModule.CliCardGroup, "Validate");
                // Neither the saved choice (the module's defaults on this fresh install) nor the runner's floor.
                string onScreenModel = ClaudeModelOtherThan(AiSettings.DefaultClaudeModel);
                string onScreenEffort = EffortOtherThan(AiSettings.DefaultClaudeEffort, CodingAgentCli.DefaultEffort);
                string said = PressWith(validate, "brainRunsOn", "Claude Code CLI",
                    "cliClaudeModel", AiBrainModule.ClaudeModelLabel(onScreenModel), "cliClaudeEffort", AiBrainModule.EffortLabel(onScreenEffort));
                FakeCliCall call = LastModelCall(rig.Fake);
                ok &= Check(sb, "aibrain cli model: Validate tests the model and effort on screen, applied or not, and names the model that answered: " + (said ?? "(no answer)"),
                    said != null &&
                    said.EndsWith(" on " + FakeCliProcess.AnsweredModelFor(onScreenModel) + " at " + onScreenEffort + " effort.", StringComparison.Ordinal) &&
                    call != null && call.After("--model") == onScreenModel && call.After("--effort") == onScreenEffort);
                ok &= Check(sb, "aibrain cli model: ...and saves neither",
                    rig.Settings.CliClaudeModel == AiSettings.DefaultClaudeModel && rig.Settings.CliClaudeEffort == AiSettings.DefaultClaudeEffort &&
                    AiSettings.Load().CliClaudeModel == AiSettings.DefaultClaudeModel);

                rig.Fake.Clear();
                PressWith(validate, "brainRunsOn", "Claude Code CLI");
                call = LastModelCall(rig.Fake);
                ok &= Check(sb, "WITNESS aibrain cli model: with no model row on screen Validate tests the saved choice",
                    call != null && call.After("--effort") == AiSettings.DefaultClaudeEffort &&
                    (AiSettings.DefaultClaudeModel.Length > 0 ? call.After("--model") == AiSettings.DefaultClaudeModel : !call.Arguments.Contains("--model")));

                rig.Runner.RefreshDetailsAsync(CodingAgentKind.Codex, CancellationToken.None).GetAwaiter().GetResult();
                pane.Load();
                rig.Fake.Clear();
                string codexSaid = PressWith(validate, "brainRunsOn", "Codex CLI", "cliCodexModel", "Vision Later", "cliCodexEffort", "Medium");
                call = LastModelCall(rig.Fake);
                ok &= Check(sb, "aibrain cli model: Validate on Codex tests the model chosen on screen by its name: " + (codexSaid ?? "(no answer)"),
                    codexSaid != null && codexSaid.EndsWith(" on vision-later at medium effort.", StringComparison.Ordinal) &&
                    call != null && call.IsCodex && call.After("-m") == "vision-later" && call.Arguments.Contains("model_reasoning_effort=medium"));

                // Review finding F14: an install that never chose runs on the module's default, so a refusal of it says it is
                // the default and what else the card offers, in Validate and in an audition's samples alike. Claude Code's
                // own words for a model it will not serve, as 2.1.293 wrote them (measured 2026-10-09).
                Func<FakeCliCall, CancellationToken, Task<CliProcessResult>> answering = rig.Fake.Respond;
                rig.Fake.Respond = delegate(FakeCliCall c, CancellationToken token)
                {
                    if (!c.IsModelCall || c.IsCodex) return answering(c, token);
                    string refusedAlias = c.After("--model") ?? "";
                    return Task.FromResult(FakeCliProcess.Result(1, FakeCliProcess.ClaudeStream(
                        "There's an issue with the selected model (" + refusedAlias + "). It may not exist or you may not have access to it.",
                        true, "<synthetic>", refusedAlias), ""));
                };
                string defaultSentence = " The model " + AiSettings.DefaultClaudeModel + " is this module's default, and an organisation or a plan " +
                                         "can withhold a model: choose another one, or Claude Code's default, in the CLI card.";
                string onDefault = PressWith(validate, "brainRunsOn", "Claude Code CLI") ?? "(no answer)";
                string notDefault = ClaudeModelOtherThan(AiSettings.DefaultClaudeModel);
                string onOther = PressWith(validate, "brainRunsOn", "Claude Code CLI", "cliClaudeModel", AiBrainModule.ClaudeModelLabel(notDefault)) ?? "(no answer)";
                string audition = rig.Module.PreviewDispositionAsync(false,
                    new Dictionary<string, string>(StringComparer.Ordinal) { { "brainRunsOn", "Claude Code CLI" } }).GetAwaiter().GetResult() ?? "(no audition)";
                rig.Fake.Respond = answering;
                // With Claude Code's own default as the module's, there is no model of the module's to name.
                ok &= Check(sb, "aibrain cli model: Validate on the module's default model, refused, ends by saying it is the default and what else the card offers: " + onDefault,
                    onDefault.StartsWith("✗ Claude Code refused ", StringComparison.Ordinal) &&
                    (AiSettings.DefaultClaudeModel.Length == 0 ? !onDefault.Contains("this module's default") : onDefault.EndsWith(defaultSentence, StringComparison.Ordinal)));
                ok &= Check(sb, "aibrain cli model: ...and so does an audition's sample refused on it",
                    audition.Contains("✗ Claude Code refused ") &&
                    (AiSettings.DefaultClaudeModel.Length == 0 ? !audition.Contains("this module's default") : audition.Contains(defaultSentence + " · ")));
                ok &= Check(sb, "WITNESS aibrain cli model: a refused model that is not the module's default says nothing of one: " + onOther,
                    onOther.StartsWith("✗ Claude Code refused the model " + notDefault + ":", StringComparison.Ordinal) && !onOther.Contains("this module's default"));
            }
            return ok;
        }
    }
}
