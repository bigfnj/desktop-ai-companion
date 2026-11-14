using System.Globalization;
using AgentBroker.Protocol;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.AgentBroker;

// File attachments between paired brokers (lane F, 1.10.5): the master switch, one live explanation row, the per-broker
// choice on a ticked Paired row of the Brokers card, and a reveal of the attachments README. Every host member used here
// predates host 1.6.1 (SettingKind.Bool and Info, PaneAction.RevealsPath since 1.1.6), and no PaneAction.EnabledWhen or
// SettingField.VisibleWhen is set (PANE_ABI_FLOOR). Kept in its own partial so lane U's pane rewrite rebases mechanically.
public sealed partial class AgentBrokerModule
{
    internal const string AllowAttachmentsLabel = "Allow or stop attachments";
    internal const string ShowAttachmentsLabel = "Show attachments folder";
    internal const string AttachmentsAllowedHint = "attachments allowed";
    private const string AttachmentsOffRefusal = "Turn on \"Allow file attachments with paired brokers\" first, save, then press Refresh brokers.";

    private IEnumerable<SettingField> AttachmentSchemaRows() =>
    [
        new() { Id = "attachmentsEnabled", Label = "Allow file attachments with paired brokers", Kind = SettingKind.Bool, Group = "Connections" },
        new() { Id = "attachments", Label = "File attachments", Kind = SettingKind.Info, Group = "Connections", FullWidth = true }
    ];

    private IEnumerable<PaneAction> AttachmentActions() =>
    [
        new PaneAction { Label = ShowAttachmentsLabel, Group = "Connections", RevealsPath = true, InvokeAsync = () => Task.FromResult(AttachmentsReveal()) }
    ];

    private void LoadAttachmentValues(IDictionary<string, string> values) => values["attachments"] = AttachmentsText();

    // At most two sentences and 280 characters (ATTACHMENTS_INFO_ROW_TEXT), like every static pane explanation.
    internal string AttachmentsText()
    {
        const string caps = "Up to 4 files, 8 MiB each, 16 MiB per message";
        if (!settings.AttachmentsEnabled)
            return "Off. When on, an agent can send up to 4 files (8 MiB each, 16 MiB per message) to a paired broker that allows them under Brokers; received files are checked, quarantined and marked as downloaded from the internet.";
        var view = snapshot?.Attachments;
        if (view?.PolicyUnreadable == true)
            return "On, but the allow list could not be read, so no broker may send files here until you allow one under Brokers. " + caps + "; received files are quarantined and marked as downloaded from the internet.";
        int allowed = view?.AllowedBrokers.Length ?? 0;
        int files = view?.InboxFiles ?? 0;
        string size = ((view?.InboxBytes ?? 0) / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture);
        return "On: " + allowed.ToString(CultureInfo.InvariantCulture) + (allowed == 1 ? " paired broker may" : " paired brokers may") +
            " send files here (allow or stop one under Brokers). " + caps + "; " + files.ToString(CultureInfo.InvariantCulture) +
            (files == 1 ? " received file" : " received files") + " (" + size + " MiB) quarantined and marked as downloaded from the internet.";
    }

    // The README the worker writes when attachments are on, under this module's own storage, which is the only place the
    // host lets RevealsPath open. The path is built here, never taken from the worker.
    private string AttachmentsReveal()
    {
        if (dataDirectory is null || !settings.AttachmentsEnabled)
            return "\u2717 " + AttachmentsOffRefusal;
        string readme = Path.Combine(dataDirectory, "attachments", "README.txt");
        return File.Exists(readme) ? readme : "\u2717 The attachments folder appears once the broker has restarted with attachments on. Press Refresh status.";
    }

    private static bool AttachmentsAllowed(PeerView peer, BrokerSnapshot? view) =>
        view?.Attachments?.AllowedBrokers.Contains(peer.BrokerId) == true;

    private static string AttachmentPairedSuffix(PeerView peer, BrokerSnapshot view) =>
        AttachmentsAllowed(peer, view) ? ", " + AttachmentsAllowedHint : "";

    // One ticked Paired row, still paired under the same certificate. The owner's choice toggles that broker's permission;
    // the worker pins it to the broker's current certificate and a revoke removes it (D-9).
    private async Task<string> ToggleAttachmentsFromListAsync(PaneAction pressed)
    {
        if (BrokerActionUnavailable() is { } unavailable) return unavailable;
        if (!settings.AttachmentsEnabled || snapshot!.Attachments is not { Enabled: true }) return AttachmentsOffRefusal;
        if (TickedBrokerRow(PairedGroup, out string refusal) is not { } row) return refusal;
        PairingPeerIdentity? identity = PairingIdentity(row.Peer);
        if (snapshot.Paired.All(peer => PairingIdentity(peer) != identity))
            return "This broker is no longer paired. Press Refresh brokers to see the current state.";
        bool allow = !AttachmentsAllowed(row.Peer, snapshot);
        int current = generation;
        ControlResponse? response = await SendAsync(new("peer-attachments", row.Peer.BrokerId, Value: allow));
        pressed.ReloadPaneAfter = true;
        if (shutDown || generation != current) return BrokerActionRestarted;
        if (response?.Success != true) return "The broker could not change attachments for " + BrokerRowName(row.Peer) + ". Press Refresh brokers and check it again.";
        return allow
            ? "Attachments allowed from " + BrokerRowName(row.Peer) + ". Its agents can send files here; each is checked, quarantined and marked as downloaded from the internet."
            : "Attachments from " + BrokerRowName(row.Peer) + " are no longer accepted.";
    }

    // Lane F module checks, through the real Init, pane, ListCard and worker argument builder. Each label has a killing
    // mutation recorded in the lane F commit.
    private static void RunAttachmentChecks(Action<bool, string> check, TestDispatcher dispatcher)
    {
        var defaults = BrokerSettings.Read(new TestHost().SettingsFor(Id));
        string[] Launch(BrokerSettings value)
        {
            var start = new System.Diagnostics.ProcessStartInfo();
            WorkerSupervisor.AddArguments(start, new("worker.exe", @"C:\synthetic-data", value, 1, 1));
            return start.ArgumentList.ToArray();
        }
        check(!defaults.AttachmentsEnabled && !Launch(defaults).Contains("--attachments") &&
            Launch(defaults with { AttachmentsEnabled = true }).SkipWhile(argument => argument != "--attachments").Take(2).SequenceEqual(["--attachments", "true"]) &&
            BrokerSettings.TryEdit(defaults, new Dictionary<string, string> { ["attachmentsEnabled"] = "true" }, out var edited) && edited.AttachmentsEnabled,
            "ATTACHMENTS_SETTING_DEFAULT_OFF_AND_FLAG: attachments default off, and only an explicit on launches the worker with --attachments true");

        var host = new TestHost();
        string storage = Path.Combine(Path.GetTempPath(), "agentbroker-attachments-synthetic-" + Guid.NewGuid().ToString("N"));
        host.UseStorage(Id, new TestStorage(storage));
        host.SettingsFor(Id).Set("enabled", "true");
        host.SettingsFor(Id).Set("attachmentsEnabled", "true");
        var a = new PeerView(Guid.NewGuid(), "Alpha", "https://192.0.2.41:7433/", new string('A', 64));
        var b = new PeerView(Guid.NewGuid(), "Bravo", "https://192.0.2.42:7433/", new string('B', 64));
        var c = new PeerView(Guid.NewGuid(), "Charlie", "https://192.0.2.43:7433/", new string('C', 64));
        var view = new BrokerSnapshot(Guid.NewGuid(), "Synthetic", "http://127.0.0.1:7432", "https://192.0.2.40:7433/",
            [c], [a, b], [], [], new Dictionary<string, string>(), new string('E', 64),
            Attachments: new AttachmentsView(true, [], 0, 0, null, false));
        var control = new PaneControl();
        var module = new AgentBrokerModule(control, new StatusSupervisor(view)) { ClientSetup = new(new TestClientCommand()) };
        try
        {
            module.Init(host); Pump(() => module.snapshot is not null, dispatcher);
            OptionsPane pane = host.OptionsPanes.Single();
            ListCard card = pane.Lists!.Single();
            module.ApplyStatus(new(true, false, "standby", view));
            string Press(Func<ListItem, bool> pick) => PressBrokerAction(pane, AllowAttachmentsLabel, pick);
            bool IsPaired(ListItem item, PeerView peer) => item.Group == PairedGroup && item.Id.Contains(peer.BrokerId.ToString("N"), StringComparison.Ordinal);

            string none = Press(_ => false);
            string discovered = Press(item => item.Group == DiscoveredGroup);
            string both = Press(item => item.Group == PairedGroup);
            check(none.StartsWith("Tick one broker", StringComparison.Ordinal) && discovered.StartsWith("This action needs a broker under", StringComparison.Ordinal) &&
                both.StartsWith("Tick exactly one broker", StringComparison.Ordinal) && control.Requests.All(request => request.Operation != "peer-attachments"),
                "ATTACHMENTS_ALLOW_ACTION_ONE_PAIRED_ROW: the action acts on exactly one ticked Paired row and sends nothing otherwise");
            string allowed = Press(item => IsPaired(item, a));
            check(control.Requests.LastOrDefault() is { Operation: "peer-attachments", Value: true } sent && sent.Id == a.BrokerId &&
                allowed.StartsWith("Attachments allowed from", StringComparison.Ordinal),
                "ATTACHMENTS_ALLOW_ACTION_ONE_PAIRED_ROW: one ticked Paired row sends peer-attachments for exactly that broker");
            module.ApplyStatus(new(true, false, "standby", view with { Attachments = new AttachmentsView(true, [a.BrokerId], 2, 3 * 1024 * 1024, null, false) }));
            var rows = card.LoadItems();
            string stopped = Press(item => IsPaired(item, a));
            check(rows.Single(item => IsPaired(item, a)).Detail.EndsWith(", " + AttachmentsAllowedHint, StringComparison.Ordinal) &&
                !rows.Single(item => IsPaired(item, b)).Detail.Contains(AttachmentsAllowedHint, StringComparison.Ordinal) &&
                control.Requests.LastOrDefault() is { Operation: "peer-attachments", Value: false } && stopped.EndsWith("are no longer accepted.", StringComparison.Ordinal),
                "ATTACHMENTS_ALLOW_ACTION_ONE_PAIRED_ROW: an allowed broker says so in its row, and the same action stops it");

            string on = module.AttachmentsText();
            module.settings = module.settings with { AttachmentsEnabled = false };
            string off = module.AttachmentsText();
            int requests = control.Requests.Count;
            string refused = Press(item => IsPaired(item, a));
            check(new[] { on, off }.All(text => text.Length <= 280) &&
                on.StartsWith("On: 1 paired broker may", StringComparison.Ordinal) && on.Contains("2 received files (3 MiB)", StringComparison.Ordinal) &&
                off.StartsWith("Off.", StringComparison.Ordinal) && refused == AttachmentsOffRefusal && control.Requests.Count == requests,
                "ATTACHMENTS_INFO_ROW_TEXT: the row states off or on, the allowed brokers, the caps and the quarantine in at most 280 characters");

            module.settings = module.settings with { AttachmentsEnabled = true };
            string missing = pane.Actions.Single(action => action.Label == ShowAttachmentsLabel).InvokeAsync().GetAwaiter().GetResult();
            string readme = Path.Combine(storage, "attachments", "README.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(readme)!);
            File.WriteAllText(readme, "Synthetic README.");
            string revealed = pane.Actions.Single(action => action.Label == ShowAttachmentsLabel).InvokeAsync().GetAwaiter().GetResult();
            check(pane.Actions.Single(action => action.Label == ShowAttachmentsLabel).RevealsPath && missing.StartsWith("\u2717", StringComparison.Ordinal) &&
                revealed == readme,
                "ATTACHMENTS_REVEAL_FILE: the action reveals the README file inside the module's storage, and says why when it does not exist");
        }
        finally
        {
            module.Shutdown(); Pump(() => module.pending.IsCompleted, dispatcher);
            try { if (Directory.Exists(storage)) Directory.Delete(storage, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
