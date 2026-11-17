using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AgentBroker.Protocol;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.AgentBroker;

// PANE-TEXT (1.10.5): short static text, the consent row beside its switch, the compact client summary and the offline guide,
// through the real module Init, pane and actions. Each label below has a killing mutation in tests/mutate-selftest-guards.py
// ("broker-pane: text: ...").
public sealed partial class AgentBrokerModule
{
    private static int Sentences(string text) => Regex.Matches(text, @"[.!?](\s|$)").Count;

    private static void RunPaneTextChecks(Action<bool, string> check, TestDispatcher dispatcher)
    {
        RunStaticTextChecks(check, dispatcher);
        RunClientSummaryChecks(check, dispatcher);
        RunGuideChecks(check, dispatcher);
    }

    private static void RunStaticTextChecks(Action<bool, string> check, TestDispatcher dispatcher)
    {
        var host = new TestHost();
        host.UseStorage(Id, new TestStorage(Path.Combine(Path.GetTempPath(), "agentbroker-pane-text-synthetic")));
        var module = new AgentBrokerModule(new StatusControl(), new TestSupervisor());
        try
        {
            module.Init(host);
            Pump(() => module.pending.IsCompleted, dispatcher);
            var pane = host.OptionsPanes.Single();
            var values = pane.Load();
            string[] tooLong = PaneTextRows.Where(id => !values.TryGetValue(id, out string? text) || text.Length == 0 ||
                text.Length > 280 || Sentences(text) > 2 || !pane.Schema.Any(field => field.Id == id && field.Kind == SettingKind.Info)).ToArray();
            // Removal contracts have dedicated owners: BROKERS_ONE_CARD, lifetime help and WORDING_DELIVERY_STATES.
            // This check owns text length and sentence count, so restoring a removed row does not masquerade as length failure.
            check(PaneTextRows.SequenceEqual(new[] { "claudeWakeConsent", "desktopChatHelp" }, StringComparer.Ordinal) &&
                tooLong.Length == 0,
                "PANE_TEXT_SHORT: each static explanation row is at most two sentences and 280 characters" +
                (tooLong.Length == 0 ? "" : " (over: " + string.Join(", ", tooLong) + ")"));

            var schema = pane.Schema.ToList();
            int toggle = schema.FindIndex(field => field.Id == "claudePeerCredentials");
            int consent = schema.FindIndex(field => field.Id == "claudeWakeConsent");
            check(toggle >= 0 && consent == toggle + 1 && schema[consent] is { Kind: SettingKind.Info, Group: "Coding agents" } &&
                values.TryGetValue("claudeWakeConsent", out string? shown) &&
                shown.Contains("sensitive local peer credentials", StringComparison.Ordinal) &&
                shown.Contains("permission controls still apply", StringComparison.Ordinal),
                "CONSENT_BESIDE_SWITCH: the Claude wake-up consent is the row directly under its switch and names both facts");
        }
        finally
        {
            module.Shutdown();
            Pump(() => module.pending.IsCompleted, dispatcher);
        }
    }

    private static void RunClientSummaryChecks(Action<bool, string> check, TestDispatcher dispatcher)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var wakeable = new SessionView("claude:" + Guid.NewGuid().ToString("D"), Guid.NewGuid(), "Synthetic Claude", "claude-native", "Idle", true, true, false, false);
        var mailbox = new SessionView("mcp:" + Guid.NewGuid().ToString("D"), Guid.NewGuid(), "Synthetic", "mcp", "Idle", true, false, false, false);
        var mcp = new McpStatusView(true, now, now, [new(McpClientFamily.Other), new(McpClientFamily.Claude, LastInitializeUtc: now, LastToolsListUtc: now),
            new(McpClientFamily.Codex)], [new(mailbox.SessionId, mailbox.Incarnation, McpClientFamily.Claude)]);
        var view = new BrokerSnapshot(Guid.NewGuid(), "Synthetic", "http://127.0.0.1:7432", null, [], [], [], [wakeable, mailbox],
            new Dictionary<string, string>(), Mcp: mcp);
        var host = new TestHost();
        host.UseStorage(Id, new TestStorage(Path.Combine(Path.GetTempPath(), "agentbroker-client-summary-synthetic")));
        host.SettingsFor(Id).Set("enabled", "true");
        host.SettingsFor(Id).Set("claudePeerCredentials", "true");
        var module = new AgentBrokerModule(new StatusControl(), new StatusSupervisor(view)) { McpNow = () => now, ClientSetup = new(new TestClientCommand()) };
        try
        {
            module.Init(host);
            Pump(() => module.snapshot is not null, dispatcher);
            module.claudeInspection = new ClientInspection(ClientConfiguration.Configured, "user", "http", "connected", 0, now);
            string healthy = module.ClientDisplay("claude");
            check(!healthy.Contains('\n', StringComparison.Ordinal) && healthy.StartsWith("Claude Code: configured, with an inbox registered", StringComparison.Ordinal) &&
                new[] { "✓ broker", "✓ config", "✓ endpoint", "✓ tools", "✓ inbox 1", "✓ wake 1/1" }
                    .All(token => healthy.Contains(token, StringComparison.Ordinal)) &&
                !healthy.Contains("✗", StringComparison.Ordinal) && !healthy.Contains("⚠", StringComparison.Ordinal),
                "CLIENT_SUMMARY_COMPACT: a healthy client is one line that starts with its name, with a glyph and token per state");
            string[] tray = module.ClientStateLines("claude").ToArray();
            check(tray.Length == 6 && tray.All(line => line.StartsWith('✓')) &&
                tray.Any(line => line.StartsWith("✓ Native wake on: 1 of 1", StringComparison.Ordinal)),
                "CLIENT_SUMMARY_COMPACT: the tray still lists every state of a healthy client as its own sentence");

            module.claudeInspection = new ClientInspection(ClientConfiguration.Absent, CheckedUtc: now);
            string[] absent = module.ClientDisplay("claude").Split('\n');
            check(absent.Length == 2 && absent[0].StartsWith("Claude Code: not configured.", StringComparison.Ordinal) &&
                absent[0].Contains("✗ config", StringComparison.Ordinal) &&
                absent[1].StartsWith("✗ Configuration: absent.", StringComparison.Ordinal) &&
                absent.Count(line => line.Contains("Connect Claude MCP", StringComparison.Ordinal)) == 1,
                "CLIENT_SUMMARY_COMPACT: an error state adds exactly one line with its glyph and its action");

            module.ApplyStatus(new(true, true, "running", view with { Mcp = mcp with { Clients = [new(McpClientFamily.Other),
                new(McpClientFamily.Claude), new(McpClientFamily.Codex)] } }));
            module.claudeInspection = new ClientInspection(ClientConfiguration.Configured, "user", "http", "connected", 0, now);
            string[] quiet = module.ClientDisplay("claude").Split('\n');
            string[] quietTray = module.ClientStateLines("claude").ToArray();
            const string noTraffic = "⚠ Tools: no traffic from this client since the broker started.";
            check(quiet.Length == 2 && quiet[0].Contains("⚠ tools", StringComparison.Ordinal) &&
                quiet[1] == noTraffic + " " + ReconnectAction("claude") && ReconnectAction("claude").Contains("/mcp", StringComparison.Ordinal) &&
                quietTray.Contains(noTraffic),
                "CLIENT_SUMMARY_COMPACT: a Tools warning keeps a short reconnect action in the pane, and the tray keeps its sentence");
        }
        finally
        {
            module.Shutdown();
            Pump(() => module.pending.IsCompleted, dispatcher);
        }
    }

    private static void RunGuideChecks(Action<bool, string> check, TestDispatcher dispatcher)
    {
        string[] names = typeof(AgentBrokerModule).Assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith("." + GuideResourceName, StringComparison.Ordinal)).ToArray();
        byte[]? bytes = GuideBytes();
        string guide = bytes is null ? "" : Encoding.UTF8.GetString(bytes);
        string[] missing = GuideSections.Where(section => !guide.Contains("id=\"" + section + "\"", StringComparison.Ordinal)).ToArray();
        // The release watch set excludes Markdown, so the compiled guide must stay the Guide folder's .html; whether the watch
        // set covers that source is a packaging check (lane evidence p2, RELEASE-CHECK-PROPOSAL.md).
        check(names.Length == 1 && names[0].EndsWith(".Guide." + GuideResourceName, StringComparison.Ordinal) &&
            bytes is { Length: > 0 } && GuideSections.Length == 6 && missing.Length == 0 &&
            guide.StartsWith("<!doctype html>", StringComparison.OrdinalIgnoreCase) &&
            !guide.Contains("<script", StringComparison.OrdinalIgnoreCase) && !guide.Contains("<link", StringComparison.OrdinalIgnoreCase) &&
            !guide.Contains(" src=", StringComparison.OrdinalIgnoreCase),
            "GUIDE_EMBEDDED: the module assembly carries one self-contained HTML guide with every section the pane points to" +
            (missing.Length == 0 ? "" : " (missing: " + string.Join(", ", missing) + ")"));

        RunGuideRecipeChecks(check, guide);
        RunGuideContractChecks(check, guide);

        string root = Path.Combine(Path.GetTempPath(), "agentbroker-guide-synthetic-" + Guid.NewGuid().ToString("N"));
        string storage = Path.Combine(root, "storage"), linked = Path.Combine(root, "linked"), outside = Path.Combine(root, "outside");
        string junction = Path.Combine(linked, GuideFolder);
        var opened = new List<string>();
        var host = new TestHost();
        host.UseStorage(Id, new TestStorage(storage));
        var module = new AgentBrokerModule(new StatusControl(), new TestSupervisor()) { GuideOpener = path => { opened.Add(path); return true; } };
        try
        {
            Directory.CreateDirectory(storage);
            module.Init(host);
            Pump(() => module.pending.IsCompleted, dispatcher);
            var pane = host.OptionsPanes.Single();
            PaneAction action = pane.Actions.Single(item => item.Label == GuideActionLabel);
            string result = action.InvokeAsync!().GetAwaiter().GetResult();
            string expected = Path.Combine(storage, GuideFolder, GuideResourceName);
            check(action.Group == "AgentBroker" && opened.Count == 1 && opened[0] == expected && File.Exists(expected) &&
                bytes is not null && File.ReadAllBytes(expected).AsSpan().SequenceEqual(bytes) &&
                result.Contains(expected, StringComparison.Ordinal) && !result.Contains("is open", StringComparison.OrdinalIgnoreCase),
                "GUIDE_ACTION: the guide is copied into module storage and the starter opens exactly that copy, once");
            check(GuideTarget(storage, GuideFolder) == expected && GuideTarget(storage, "..") is null &&
                GuideTarget(storage, Path.Combine("..", "outside")) is null,
                "GUIDE_ACTION: a guide folder that leaves module storage is refused");

            // A guide folder that is a junction to somewhere else: nothing is written through it and nothing is opened.
            Directory.CreateDirectory(linked); Directory.CreateDirectory(outside);
            using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + junction + "\" \"" + outside + "\"")
                { StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                  UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
            {
                mklink?.WaitForExit(10000);
            }
            bool made = Directory.Exists(junction) && (File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0;
            check(made, "WITNESS: GUIDE_ACTION junction fixture was created (a missing fixture fails here, never skips the next check)");
            int before = opened.Count;
            string refused = CopyAndOpenGuide(linked, bytes ?? [1], path => { opened.Add(path); return true; });
            check(made && opened.Count == before && !File.Exists(Path.Combine(outside, GuideResourceName)) &&
                refused.StartsWith("The guide was not opened", StringComparison.Ordinal),
                "GUIDE_ACTION: a guide folder that is a link is refused, and the starter is not called");
        }
        finally
        {
            module.Shutdown();
            Pump(() => module.pending.IsCompleted, dispatcher);
            try { if (Directory.Exists(junction)) Directory.Delete(junction); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
