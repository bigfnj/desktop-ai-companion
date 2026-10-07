using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DesktopAICompanion.Ai;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.AiBrainModule
{
    /// <summary>
    /// Lane feature/layout-aibrain (aibrain 1.3.0 on host 1.4.0): the pane as the owner's approved mockup AB2 draws it,
    /// built with the host's 1.4.0 settings primitives. Each card's CardEnabledWhen by exact string is pinned field by
    /// field in AiEngineProbe.Cli.cs (CheckCliPaneLayout, re-pointed by this lane); this part pins the rest: the gate sits
    /// on a card's first field and nowhere else, no row carries a gate of its own, the cards every engine uses carry
    /// none, each engine card's buttons sit in the card that greys them, the fullscreen stand-down sits where a CLI can
    /// still reach it, the OCR engine is a path field whose pick still reaches the live brain, each slot card's Test
    /// connection tests its own slot, the host floor, and an existing settings file that a pane round trip leaves byte for
    /// byte as it was, in each engine state. No model is called, no CLI is started (the runner's process seam is a fake),
    /// and nothing reaches a network: every Test connection pressed here answers from the endpoint policy or the consent
    /// switch before any request, and the seeds keep the local slot at a loopback port nothing listens on with no warm-up.
    /// </summary>
    public static partial class AiEngineProbe
    {
        internal static bool RunLayout(StringBuilder sb)
        {
            bool ok = true;
            ok &= GuardedCheck(sb, "CheckLayoutCards", CheckLayoutCards);
            ok &= GuardedCheck(sb, "CheckLayoutOcrPathField", CheckLayoutOcrPathField);
            ok &= GuardedCheck(sb, "CheckLayoutTestConnection", CheckLayoutTestConnection);
            ok &= GuardedCheck(sb, "CheckLayoutRoundTrip", CheckLayoutRoundTrip);
            return ok;
        }

        /// <summary>The cards an engine choice greys, by group, with the condition AB2 draws for each (its cw).</summary>
        private static readonly string[][] GatedCards =
        {
            new[] { AiBrainModule.CliCardGroup, "brainRunsOn=Claude Code CLI|Codex CLI" },
            new[] { "Local provider", "brainRunsOn=Local model|Cloud provider" },
            new[] { "Local server (Ollama only)", "brainRunsOn=Local model|Cloud provider" },
            new[] { "Cloud provider", "brainRunsOn=Cloud provider" },
        };

        /// <summary>The cards every engine uses: nothing may grey them.</summary>
        private static readonly string[] UngatedCards = { "Status", "AI brain", "Persona", "Triggers", "What it sees" };

        /// <summary>Each button that serves one engine, as (card, label): it greys with that card and nowhere else.</summary>
        private static readonly string[][] EngineButtons =
        {
            new[] { AiBrainModule.CliCardGroup, "Validate" },
            new[] { AiBrainModule.CliCardGroup, "Update CLI" },
            new[] { "Local provider", "Refresh local models" },
            new[] { "Local provider", "Test connection" },
            new[] { "Cloud provider", "Test connection" },
            new[] { "Cloud provider", "Refresh cloud models" },
        };

        /// <summary>The gate where the host reads it, the stray gates it would ignore, the cards that must stay live, the
        /// buttons' cards, the stand-down's place, and the host floor.</summary>
        private static bool CheckLayoutCards(StringBuilder sb)
        {
            bool ok = true;
            using (var rig = new CliRig("aibrain-layout-cards", ""))
            {
                OptionsPane pane = rig.Pane;
                ok &= Check(sb, "aibrain layout: the module asks for host 1.4.0, whose CardEnabledWhen and path field its pane sets: " +
                    rig.Module.Info.MinHostVersion, rig.Module.Info.MinHostVersion == "1.4.0");

                var misgated = new List<string>();
                foreach (string[] card in GatedCards)
                {
                    SettingField lead = FirstFieldOfGroup(pane, card[0]);
                    if (lead == null || lead.CardEnabledWhen != card[1]) misgated.Add(card[0] + " (" + (lead == null ? "no card" : lead.CardEnabledWhen ?? "none") + ")");
                }
                ok &= Check(sb, "aibrain layout: the CLI, Local provider, Local server and Cloud provider cards each carry AB2's condition on their first field" +
                    (misgated.Count > 0 ? ": " + string.Join(", ", misgated) : ""), misgated.Count == 0);

                // The host reads CardEnabledWhen from a card's first field and ignores it anywhere else, so a copy further
                // down would read as a gate and gate nothing; and a row's own EnabledWhen that repeats or widens the card's
                // would be a second condition that can drift from it. A row may carry one only when it is STRICTLY NARROWER
                // than its card's, on the same field (aibrain 1.3.1: the sign-in token row, live on Claude Code alone
                // inside a card live on either CLI), which is the schema's own rule that a field is never live while
                // nothing reads it.
                var stray = new List<string>();
                var rowGates = new List<string>();
                var narrowed = new List<string>();
                if (pane != null && pane.Schema != null)
                    foreach (SettingField f in pane.Schema)
                    {
                        if (f == null) continue;
                        if (!string.IsNullOrEmpty(f.CardEnabledWhen) && !ReferenceEquals(FirstFieldOfGroup(pane, f.Group), f)) stray.Add(f.Id);
                        if (string.IsNullOrEmpty(f.EnabledWhen)) continue;
                        SettingField lead = FirstFieldOfGroup(pane, f.Group);
                        if (IsStrictlyNarrower(f.EnabledWhen, lead == null ? null : lead.CardEnabledWhen)) narrowed.Add(f.Id);
                        else rowGates.Add(f.Id);
                    }
                ok &= Check(sb, "aibrain layout: no field but a card's first carries a CardEnabledWhen, which the host would ignore" +
                    (stray.Count > 0 ? ": " + string.Join(", ", stray) : ""), stray.Count == 0);
                ok &= Check(sb, "aibrain layout: no row carries an EnabledWhen of its own unless it is strictly narrower than its card's gate" +
                    (rowGates.Count > 0 ? ": " + string.Join(", ", rowGates) : ""), rowGates.Count == 0);
                ok &= Check(sb, "WITNESS aibrain layout: the sign-in token row is the one row narrower than its card: " + string.Join(", ", narrowed),
                    narrowed.Count == 1 && narrowed[0] == "cliToken");

                var gatedAlways = new List<string>();
                foreach (string g in UngatedCards)
                {
                    SettingField lead = FirstFieldOfGroup(pane, g);
                    if (lead == null || !string.IsNullOrEmpty(lead.CardEnabledWhen)) gatedAlways.Add(g);
                }
                ok &= Check(sb, "WITNESS aibrain layout: Status, AI brain, Persona, Triggers and What it sees carry no card gate" +
                    (gatedAlways.Count > 0 ? ": " + string.Join(", ", gatedAlways) : ""), gatedAlways.Count == 0);

                // AB2 draws every card open and has no list card: nothing here is collapsible.
                var collapsible = new List<string>();
                if (pane != null && pane.Schema != null)
                    foreach (SettingField f in pane.Schema)
                        if (f != null && (f.Collapsible || f.StartCollapsed)) collapsible.Add(f.Id);
                ok &= Check(sb, "aibrain layout: no card is collapsible and there is no list card, as AB2 draws the pane" +
                    (collapsible.Count > 0 ? ": " + string.Join(", ", collapsible) : ""),
                    collapsible.Count == 0 && (pane == null || pane.Lists == null || pane.Lists.Count == 0));

                // A button greys with the card it is declared in, so one that serves an engine must sit in that engine's card.
                var misplaced = new List<string>();
                foreach (string[] button in EngineButtons)
                    if (FindAction(pane, button[0], button[1]) == null) misplaced.Add(button[1] + " in " + button[0]);
                ok &= Check(sb, "aibrain layout: every button that serves one engine sits in the card that engine greys" +
                    (misplaced.Count > 0 ? ": missing " + string.Join(", ", misplaced) : ""), misplaced.Count == 0);

                // The fullscreen stand-down is read on every engine, a CLI included, and a greyed card disables every row it
                // holds, so it sits in a card nothing greys: Triggers, under the Ask hotkey it declines.
                SettingField standDown = FieldOf(pane, "standDownFullscreen");
                ok &= Check(sb, "aibrain layout: the fullscreen stand-down sits in Triggers under the Ask hotkey, live on every engine (" +
                    (standDown == null ? "absent" : standDown.Group) + ")",
                    standDown != null && standDown.Group == "Triggers" && IndexOfField(pane, "hotkey") >= 0 &&
                    IndexOfField(pane, "hotkey") < IndexOfField(pane, "standDownFullscreen") && CardGateOf(pane, "standDownFullscreen").Length == 0);
            }
            return ok;
        }

        /// <summary>The OCR engine as a host 1.4.0 path field, "Choose OCR engine…" gone, and what that button did still
        /// reachable: the pick, saved by Apply, is the path the live brain resolves.</summary>
        private static bool CheckLayoutOcrPathField(StringBuilder sb)
        {
            bool ok = true;
            using (var rig = new CliRig("aibrain-layout-ocr", ""))
            {
                OptionsPane pane = rig.Pane;
                SettingField ocr = FieldOf(pane, "tesseractPath");
                ok &= Check(sb, "aibrain layout: the OCR engine is a path field in What it sees, for an .exe, saying \"(auto-detect)\" while blank: " +
                    (ocr == null ? "absent" : ocr.Kind + " \"" + ocr.Label + "\" [" + string.Join(",", ocr.FileExtensions ?? new string[0]) + "] " + ocr.EmptyHint),
                    ocr != null && ocr.Kind == SettingKind.FilePath && ocr.Label == "OCR engine (tesseract.exe)" && ocr.Group == "What it sees" &&
                    ocr.FileExtensions != null && string.Join("|", ocr.FileExtensions) == "exe" && ocr.EmptyHint == "(auto-detect)");

                bool chooser = false;
                if (pane != null && pane.Actions != null)
                    foreach (PaneAction a in pane.Actions)
                        if (a != null && a.Label != null && a.Label.StartsWith("Choose OCR engine", StringComparison.Ordinal)) chooser = true;
                ok &= Check(sb, "aibrain layout: \"Choose OCR engine…\" is gone; the OCR engine field's own Browse replaces it", !chooser);
                PaneAction testOcr = FindAction(pane, "What it sees", "Test OCR");
                ok &= Check(sb, "WITNESS aibrain layout: What it sees keeps Get Tesseract… and Test OCR, which reads the field's unapplied pick",
                    FindAction(pane, "What it sees", "Get Tesseract…") != null && testOcr != null && testOcr.InvokeWithPendingAsync != null);

                // What the button did, by the field: a path put in the field and applied is stored whole and is the engine
                // the live brain resolves. A one-byte tesseract.exe in a scratch folder stands in for the one picked.
                string dir = Path.Combine(Path.GetTempPath(), "dp-aibrain-layout-ocr-" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(dir);
                    string picked = Path.Combine(dir, "tesseract.exe");
                    File.WriteAllBytes(picked, new byte[] { 0 });
                    SpinWait.SpinUntil(delegate { return rig.Module.SessionForDiagnostics.LiveBrainForDiagnostics != null; }, TimeSpan.FromSeconds(5));
                    AiBrain before = rig.Module.SessionForDiagnostics.LiveBrainForDiagnostics;
                    bool saved = rig.Save("tesseractPath", picked);
                    AiBrain after = null;
                    SpinWait.SpinUntil(delegate
                    {
                        after = rig.Module.SessionForDiagnostics.LiveBrainForDiagnostics;
                        return after != null && !ReferenceEquals(after, before);
                    }, TimeSpan.FromSeconds(5));
                    string resolved = after != null && !ReferenceEquals(after, before) ? after.ResolveTesseractForDiagnostics() : null;
                    ok &= Check(sb, "aibrain layout: a path put in the OCR engine field is saved whole by Apply and is the engine the live brain resolves (" +
                        (resolved ?? "no new brain") + ")",
                        before != null && saved && rig.Settings.TesseractPath == picked && AiSettings.Load().TesseractPath == picked &&
                        string.Equals(resolved, picked, StringComparison.OrdinalIgnoreCase));
                }
                finally
                {
                    try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
                    catch { ok &= Check(sb, "layout OCR scratch cleanup", false); }
                }
            }
            return ok;
        }

        /// <summary>Press an action's pending-aware delegate with these on-screen values (id, value pairs).</summary>
        private static string PressWith(PaneAction action, params string[] pairs)
        {
            if (action == null || action.InvokeWithPendingAsync == null) return null;
            var pending = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i + 1 < pairs.Length; i += 2) pending[pairs[i]] = pairs[i + 1];
            Task<string> pressed = action.InvokeWithPendingAsync(pending);
            return pressed.Wait(TimeSpan.FromSeconds(20)) ? pressed.Result : null;
        }

        /// <summary>Each slot card's Test connection tests its own slot. Until this lane there was one, in Cloud provider,
        /// testing whichever slot was active, so greying that card off the cloud would have taken the local slot's only
        /// test away. Every press answers before a request: "not a url" is refused by the endpoint policy, and the cloud
        /// slot's preset endpoint by the consent switch, which these presses leave off.</summary>
        private static bool CheckLayoutTestConnection(StringBuilder sb)
        {
            bool ok = true;
            string policyError;
            string unusedNormalized;
            AiEndpointPolicy.TryNormalize("not a url", out unusedNormalized, out policyError);
            string localRefused = "✗ " + policyError;
            const string NoConsent = "✗ Approve cloud data sharing first.";
            using (var rig = new CliRig("aibrain-layout-test", ""))
            {
                OptionsPane pane = rig.Pane;
                PaneAction local = FindAction(pane, "Local provider", "Test connection");
                PaneAction cloud = FindAction(pane, "Cloud provider", "Test connection");
                ok &= Check(sb, "aibrain layout: Local provider has a Test connection of its own, beside the Cloud provider card's",
                    local != null && cloud != null && !ReferenceEquals(local, cloud) && local.InvokeAsync != null && local.InvokeWithPendingAsync != null);

                string onLocal = PressWith(local, "brainRunsOn", "Local model", "endpoint", "not a url");
                ok &= Check(sb, "aibrain layout: the Local provider card's Test connection tests the local slot on the local model: " + onLocal,
                    onLocal == localRefused);
                string fallback = PressWith(local, "brainRunsOn", "Cloud provider", "cloudProvider", "openai", "cloudConsent", "false", "endpoint", "not a url");
                ok &= Check(sb, "aibrain layout: on the cloud the Local provider card's Test connection tests the local slot, the fallback: " + fallback,
                    fallback == localRefused);
                string onCloud = PressWith(cloud, "brainRunsOn", "Cloud provider", "cloudProvider", "openai", "cloudConsent", "false", "endpoint", "not a url");
                ok &= Check(sb, "WITNESS aibrain layout: the Cloud provider card's Test connection tests the cloud slot on the cloud: " + onCloud,
                    onCloud == NoConsent);
                string cloudOnLocal = PressWith(cloud, "brainRunsOn", "Local model", "endpoint", "not a url");
                ok &= Check(sb, "aibrain layout: the Cloud provider card's Test connection refuses on the local model, as its greyed card says: " + cloudOnLocal,
                    cloudOnLocal == AiBrainModule.NotUsedWhileRunningOn(AiBrainModule.BrainRunsOnLocal));
                string localOnCli = PressWith(local, "brainRunsOn", "Codex CLI");
                ok &= Check(sb, "aibrain layout: the Local provider card's Test connection refuses on a CLI, as its greyed card says: " + localOnCli,
                    localOnCli == AiBrainModule.NotUsedWhileRunningOn("Codex CLI"));

                // The fallback's test is a chat to the local model, so it waits for Remembrance as the local one does; the
                // cloud's own test goes to the cloud alone.
                rig.Host.PublishContext("remembrance", RemembranceBusyFlag.Key, BusyJson("transcribing", DateTime.UtcNow));
                string heldFallback = PressWith(local, "brainRunsOn", "Cloud provider", "cloudProvider", "openai", "cloudConsent", "false", "endpoint", "not a url");
                ok &= Check(sb, "aibrain layout: on the cloud the local slot's Test connection sends nothing while Remembrance is busy: " + heldFallback,
                    heldFallback == AiBrainModule.RemembranceBusyAnswer);
                string cloudWhileBusy = PressWith(cloud, "brainRunsOn", "Cloud provider", "cloudProvider", "openai", "cloudConsent", "false");
                ok &= Check(sb, "WITNESS aibrain layout: ...while the cloud slot's Test connection is not held for Remembrance: " + cloudWhileBusy,
                    cloudWhileBusy == NoConsent);
            }
            return ok;
        }

        /// <summary>An existing settings file in each engine state, through the pane the way the host drives it.</summary>
        private static bool CheckLayoutRoundTrip(StringBuilder sb)
        {
            bool ok = true;
            ok &= RoundTripOnce(sb, "on a CLI over a cloud slot", "codex", true);
            ok &= RoundTripOnce(sb, "on the cloud", "", true);
            ok &= RoundTripOnce(sb, "on the local model with a cloud provider remembered", "", false);
            return ok;
        }

        /// <summary>
        /// Write a settings file with the module's own serializer, every field the pane shows set away from its default, as
        /// an existing install's file is; start the module on it; Load the pane, hand Save exactly what the host hands back
        /// (every editable field, a greyed card's included, since the host collects those too; Info and Header rows have no
        /// reader; a blank secret box is absent); and require the file byte for byte as it was. The WITNESS beside it
        /// changes one value and requires the file to change, so the comparison is one that can fail.
        /// </summary>
        private static bool RoundTripOnce(StringBuilder sb, string state, string cli, bool cloudPrimary)
        {
            bool ok = true;
            byte[] before = null;
            bool written = false;
            bool keyStored = false;
            string file = null;
            Action<string> writeSettings = delegate(string dir)
            {
                file = Path.Combine(dir, "ai-settings.json");
                string previous = AiPaths.SwapRoot(dir);
                try
                {
                    var s = new AiSettings
                    {
                        AiBrainEnabled = true,
                        CompanionName = "Bramble",
                        UserName = "Quill",
                        Disposition = "samuel",
                        Hotkey = "Ctrl+Alt+F9",
                        UseVision = true,
                        TesseractPath = Path.Combine("C:" + Path.DirectorySeparatorChar, "Tools", "Tesseract-OCR", "tesseract.exe"),
                        LocalBackendKind = "openai-compat",
                        Endpoint = "http://127.0.0.1:9",
                        TextModel = "rt-text",
                        VisionModel = "rt-vision",
                        AutoStartServer = false,
                        // "server" is off the default and warms nothing, so no Apply here prepares a backend.
                        ModelResidency = AiSettings.ResidencyServer,
                        StandDownForFullscreen = false,
                        StandDownForRemembrance = false,
                        CloudTextModel = "rt-cloud-text",
                        CloudVisionModel = "rt-cloud-vision",
                        CloudDataConsent = true,
                        UseLocalFallback = false,
                        CliBackend = cli,
                    };
                    s.SelectProviderEndpoint("openrouter", true);
                    string keyError;
                    keyStored = s.TrySetApiKey("ROUNDTRIP-FIXTURE-not-a-real-key", out keyError);
                    if (!cloudPrimary)
                    {
                        // A user who went back to the local model: the provider is remembered, the cloud endpoint and its
                        // key are kept, and the cloud is no longer primary.
                        s.LastCloudProvider = s.Provider;
                        s.Provider = "";
                    }
                    written = s.SaveWithin(AiSettings.ProcessLockTimeoutMilliseconds);
                    before = File.ReadAllBytes(file);
                }
                finally { AiPaths.SwapRoot(previous); }
            };

            using (var rig = new CliRig("aibrain-layout-roundtrip", "", writeSettings))
            {
                ok &= Check(sb, "aibrain layout round trip: the seed file was written by the module's serializer, its key stored, " + state,
                    written && keyStored && before != null && before.Length > 0);
                OptionsPane pane = rig.Pane;
                IReadOnlyDictionary<string, string> shown = pane == null ? null : pane.Load();
                var handedBack = new Dictionary<string, string>(StringComparer.Ordinal);
                if (pane != null && pane.Schema != null && shown != null)
                    foreach (SettingField f in pane.Schema)
                    {
                        if (f == null || f.Kind == SettingKind.Info || f.Kind == SettingKind.Header || f.Kind == SettingKind.Secret) continue;
                        string v;
                        handedBack[f.Id] = shown.TryGetValue(f.Id, out v) ? (v ?? "") : "";
                    }
                bool saved = pane != null && pane.Save(handedBack);
                byte[] after = file != null && File.Exists(file) ? File.ReadAllBytes(file) : null;
                string difference = FirstDifference(before, after);
                ok &= Check(sb, "aibrain layout: a pane Load and Save leaves an existing settings file byte for byte as it was, " + state +
                    (difference == null ? "" : " (" + difference + ")"), saved && handedBack.Count > 0 && difference == null);

                handedBack["hotkey"] = "Ctrl+Alt+F10";
                bool savedChange = pane != null && pane.Save(handedBack);
                byte[] changed = file != null && File.Exists(file) ? File.ReadAllBytes(file) : null;
                ok &= Check(sb, "WITNESS aibrain layout: ...and a changed hotkey does change the file, " + state,
                    savedChange && FirstDifference(before, changed) != null);
            }
            return ok;
        }

        /// <summary>Null when the two files are the same bytes, else the first line that differs, both sides cut short.</summary>
        private static string FirstDifference(byte[] before, byte[] after)
        {
            if (before == null || after == null) return before == after ? null : "a file is missing";
            if (before.Length == after.Length)
            {
                bool same = true;
                for (int i = 0; i < before.Length && same; i++) same = before[i] == after[i];
                if (same) return null;
            }
            string[] a = Encoding.UTF8.GetString(before).Split('\n');
            string[] b = Encoding.UTF8.GetString(after).Split('\n');
            for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                string x = i < a.Length ? a[i].Trim() : "(end)";
                string y = i < b.Length ? b[i].Trim() : "(end)";
                if (!string.Equals(x, y, StringComparison.Ordinal))
                    return "line " + (i + 1) + ": " + AiBrainModule.Ellipsize(x, 60) + " became " + AiBrainModule.Ellipsize(y, 60);
            }
            return "the bytes differ in line endings or whitespace";
        }
    }
}
