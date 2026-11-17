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
    private const string AttachmentsOffRefusal = "Turn on \"Allow file attachments with paired brokers\" first, save, then press Refresh brokers. On standby (see Status), the running broker keeps the attachments setting it was started with.";

    private IEnumerable<SettingField> AttachmentSchemaRows() =>
    [
        new() { Id = "attachmentsEnabled", Label = "Allow file attachments with paired brokers", Kind = SettingKind.Bool, Group = "Connections" },
        new() { Id = "attachments", Label = "File attachments", Kind = SettingKind.Info, Group = "Connections" }
    ];

    private IEnumerable<PaneAction> AttachmentActions() =>
    [
        new PaneAction { Label = ShowAttachmentsLabel, Group = "Connections", RevealsPath = true, InvokeAsync = () => Task.FromResult(AttachmentsReveal()) }
    ];

    private void LoadAttachmentValues(IDictionary<string, string> values) => values["attachments"] = AttachmentsText();

    internal const string AttachmentsWaitingText = "On in settings, but no running broker reports attachments on (see Status).";
    internal const string AttachmentsStillOnText = "Off in settings, but the running broker still sends files and may accept them from allowed brokers until it restarts.";
    internal const string AttachmentsUnsavedText = "On, but a change to the allow list is not saved yet. The broker retries about once a minute; a restart before then could bring back the old list.";
    internal const string AttachmentsUnsavedOffText = "Off in the running broker, and a change to the allow list is not saved yet. The broker retries about once a minute; a restart before then could bring back the old list.";
    internal const string AttachmentsWaitingRefusal = "Attachments are on in settings, but the running broker does not report them on. Press Refresh brokers in a moment; on standby (see Status), the running broker keeps the attachments setting it was started with.";

    // At most two sentences and 280 characters (ATTACHMENTS_INFO_ROW_TEXT), like every static pane explanation. Round 2
    // (F-13): On or Off is what the RUNNING broker reports (snapshot Attachments.Enabled), and the setting only explains a
    // difference, because a standby worker or a pending restart can disagree with it.
    internal string AttachmentsText()
    {
        const string caps = "Up to 4 files, 8 MiB each, 16 MiB per message";
        var view = snapshot?.Attachments;
        bool running = view?.Enabled == true;
        // Round 5 (DECISIONS 96, item 3): the worker reports an unsaved allow-list removal whatever its switch (a removal works
        // with attachments off, DECISIONS 41), so the row says so before the off, still-on, waiting and quarantine texts.
        if (view?.PolicyUnsaved == true) return view.Enabled ? AttachmentsUnsavedText : AttachmentsUnsavedOffText;
        if (!settings.AttachmentsEnabled && !running)
            return "Off. When on, an agent can send up to 4 files (8 MiB each, 16 MiB per message) to a session here or on a paired broker that has attachments on and allows this one; files accepted from a paired broker are checked, quarantined and marked as downloaded from the internet.";
        if (!settings.AttachmentsEnabled) return AttachmentsStillOnText;
        if (!running)
            return AttachmentsWaitingText + " " + caps + "; files accepted from a paired broker are quarantined and marked as downloaded from the internet.";
        // F-6: the worker could not store a received file (its data folder is not plain local NTFS, or access is denied).
        if (view?.QuarantineUnavailable == true)
            return "On, but a received file could not be stored: the broker's data folder failed its local NTFS folder checks, or access to it was denied, so files sent here are refused while that lasts. " + caps + ".";
        if (view?.PolicyUnreadable == true)
            return "On, but the allow list could not be read, so no broker may send files here until one is allowed under Brokers or a restart reads it. " + caps + "; files accepted from a paired broker are quarantined and marked as downloaded from the internet.";
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

    internal const string NoLongerPairedRefusal = "This broker is no longer paired. Press Refresh brokers to see the current state.";

    // One ticked Paired row, still paired under the same certificate. The owner's choice toggles that broker's permission;
    // the worker pins it to the broker's current certificate and a revoke removes it (D-9). DECISIONS 41 (round 2, F-23):
    // stopping an allowed broker works with attachments off; allowing one needs attachments on, in settings and in the
    // running broker.
    private async Task<string> ToggleAttachmentsFromListAsync(PaneAction pressed)
    {
        if (BrokerActionUnavailable() is { } unavailable) return unavailable;
        if (TickedBrokerRow(PairedGroup, out string refusal) is not { } row) return refusal;
        PairingPeerIdentity? identity = PairingIdentity(row.Peer);
        if (snapshot!.Paired.All(peer => PairingIdentity(peer) != identity)) return NoLongerPairedRefusal;
        bool allow = !AttachmentsAllowed(row.Peer, snapshot);
        if (allow && (!settings.AttachmentsEnabled || snapshot.Attachments is not { Enabled: true }))
            return settings.AttachmentsEnabled ? AttachmentsWaitingRefusal : AttachmentsOffRefusal;
        int current = generation;
        ControlResponse? response = await SendAsync(new("peer-attachments", row.Peer.BrokerId, Value: allow));
        pressed.ReloadPaneAfter = true;
        if (shutDown || generation != current) return BrokerActionRestarted;
        if (response?.Success != true) return "The broker did not confirm the attachments change for " + BrokerRowName(row.Peer) + ". Press Refresh brokers and check it again.";
        return allow
            ? "Attachments allowed from " + BrokerRowName(row.Peer) + ". With attachments on at its broker too, its agents can send files here; each file this broker accepts from it is checked, quarantined and marked as downloaded from the internet."
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
            // The presses use the production action factory and handler (lane U's fixture rule, AgentBrokerPaneLayoutChecks), so
            // whether the action sits in the Brokers list is BROKERS_ONE_CARD's question alone.
            PaneAction allow = module.BrokerAction(AllowAttachmentsLabel, module.ToggleAttachmentsFromListAsync);
            string InvokeAllow()
            {
                try { return allow.InvokeAsync().GetAwaiter().GetResult(); }
                catch (Exception error) { return "EXCEPTION " + error.GetType().Name; }
            }
            string Press(Func<ListItem, bool> pick)
            {
                IReadOnlyList<ListItem> items = card.LoadItems();
                foreach (var item in items) card.SetChecked(item.Id, false);
                foreach (var item in items.Where(pick)) card.SetChecked(item.Id, true);
                return InvokeAllow();
            }
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

            // F-11: the row was ticked while paired; a snapshot without that pairing arrives before the press.
            var tickedRows = card.LoadItems();
            foreach (var item in tickedRows) card.SetChecked(item.Id, item.Group == PairedGroup && IsPaired(item, b));
            module.ApplyStatus(new(true, false, "standby", view with { Paired = [a], Attachments = new AttachmentsView(true, [a.BrokerId], 2, 3 * 1024 * 1024, null, false) }));
            int beforeStale = control.Requests.Count;
            string stale = InvokeAllow();
            check(stale == NoLongerPairedRefusal && control.Requests.Count == beforeStale,
                "ATTACHMENTS_ALLOW_ACTION_STALE_PAIRING: a row ticked while paired, pressed after the pairing ended, says it is no longer paired and sends nothing");

            var onView = view with { Attachments = new AttachmentsView(true, [a.BrokerId], 2, 3 * 1024 * 1024, null, false) };
            module.ApplyStatus(new(true, false, "standby", onView));
            string on = module.AttachmentsText();
            module.ApplyStatus(new(true, false, "standby", view with { Attachments = new AttachmentsView(false, [a.BrokerId], 0, 0, null, false) }));
            module.settings = module.settings with { AttachmentsEnabled = false };
            string off = module.AttachmentsText();
            check(new[] { on, off }.All(text => text.Length <= 280) &&
                on.StartsWith("On: 1 paired broker may", StringComparison.Ordinal) && on.Contains("2 received files (3 MiB)", StringComparison.Ordinal) &&
                off.StartsWith("Off.", StringComparison.Ordinal),
                "ATTACHMENTS_INFO_ROW_TEXT: the row states off or on, the allowed brokers, the caps and the quarantine in at most 280 characters");

            // F-23 (DECISIONS 41) with the switch off: allowing a broker is refused with nothing sent; stopping the one still
            // allowed sends the removal.
            int beforeOff = control.Requests.Count;
            string refused = Press(item => IsPaired(item, b));
            bool refusedQuietly = refused == AttachmentsOffRefusal && control.Requests.Count == beforeOff;
            string removedWhileOff = Press(item => IsPaired(item, a));
            check(refusedQuietly && control.Requests.LastOrDefault() is { Operation: "peer-attachments", Value: false } removal && removal.Id == a.BrokerId &&
                removedWhileOff.EndsWith("are no longer accepted.", StringComparison.Ordinal),
                "ATTACHMENTS_ALLOW_ACTION_SWITCH_OFF: with attachments off, allowing a broker is refused and sends nothing, while stopping an allowed one still works");

            // F-13: On or Off comes from the running worker; F-12: a change there alone reaches the open pane.
            module.settings = module.settings with { AttachmentsEnabled = true };
            module.ApplyStatus(new(true, false, "standby", view with { Attachments = null }));
            string waiting = module.AttachmentsText();
            // Round 2b: with only the setting on, allowing a broker says the broker has not restarted yet, and sends nothing.
            int beforeWaiting = control.Requests.Count;
            string waitingRefusal = Press(item => IsPaired(item, b));
            check(waitingRefusal == AttachmentsWaitingRefusal && control.Requests.Count == beforeWaiting,
                "ATTACHMENTS_ALLOW_ACTION_WAITING: with attachments on in settings but not yet in the broker, allowing says to wait for the restart and sends nothing");
            module.settings = module.settings with { AttachmentsEnabled = false };
            module.ApplyStatus(new(true, false, "standby", onView));
            string stillOn = module.AttachmentsText();
            module.settings = module.settings with { AttachmentsEnabled = true };
            check(waiting.StartsWith(AttachmentsWaitingText, StringComparison.Ordinal) && waiting.Length <= 280 && stillOn == AttachmentsStillOnText,
                "ATTACHMENTS_INFO_ROW_FROM_WORKER: the row reports what the running broker does: waiting for a restart while only the setting is on, still on until a restart while only the worker is");
            module.ApplyStatus(new(true, false, "standby", onView with { Attachments = onView.Attachments! with { PolicyUnsaved = true } }));
            string unsaved = module.AttachmentsText();
            module.ApplyStatus(new(true, false, "standby", onView));
            check(unsaved == AttachmentsUnsavedText && unsaved.Length <= 280,
                "ATTACHMENTS_INFO_ROW_UNSAVED: an allow-list change the broker could not save yet is shown as not saved, with what a restart would do");
            // Round 5 (DECISIONS 96, item 3): a removal made with attachments off can fail to save too; the row says so while
            // the running broker is off, with the switch off and while only the setting is on.
            var unsavedOffView = view with { Attachments = new AttachmentsView(false, [], 0, 0, null, false) with { PolicyUnsaved = true } };
            module.settings = module.settings with { AttachmentsEnabled = false };
            module.ApplyStatus(new(true, false, "standby", unsavedOffView));
            string unsavedOff = module.AttachmentsText();
            module.settings = module.settings with { AttachmentsEnabled = true };
            string unsavedWaiting = module.AttachmentsText();
            module.ApplyStatus(new(true, false, "standby", onView));
            check(unsavedOff == AttachmentsUnsavedOffText && unsavedWaiting == AttachmentsUnsavedOffText && unsavedOff.Length <= 280,
                "ATTACHMENTS_INFO_ROW_UNSAVED_WHILE_OFF: an allow-list removal the broker could not save is shown as not saved while the running broker is off, with the switch off or only the setting on");
            int notified = 0;
            Action counter = () => notified++;
            pane.InfoChanged += counter;
            module.ApplyStatus(new(true, false, "standby", onView with { Attachments = onView.Attachments! with { InboxFiles = 3, InboxBytes = 4 * 1024 * 1024 } }));
            pane.InfoChanged -= counter;
            check(notified == 1 && pane.Load()["attachments"].Contains("3 received files (4 MiB)", StringComparison.Ordinal),
                "ATTACHMENTS_INFO_ROW_LIVE: a change only in the worker's attachment counts notifies the open pane once, and the row it reloads shows it");

            // F-11: the reveal's two refusals, exactly.
            module.settings = module.settings with { AttachmentsEnabled = false };
            string revealOff = pane.Actions.Single(action => action.Label == ShowAttachmentsLabel).InvokeAsync().GetAwaiter().GetResult();
            module.settings = module.settings with { AttachmentsEnabled = true };
            string missing = pane.Actions.Single(action => action.Label == ShowAttachmentsLabel).InvokeAsync().GetAwaiter().GetResult();
            string readme = Path.Combine(storage, "attachments", "README.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(readme)!);
            File.WriteAllText(readme, "Synthetic README.");
            string revealed = pane.Actions.Single(action => action.Label == ShowAttachmentsLabel).InvokeAsync().GetAwaiter().GetResult();
            check(pane.Actions.Single(action => action.Label == ShowAttachmentsLabel).RevealsPath && revealOff == "\u2717 " + AttachmentsOffRefusal &&
                missing == "\u2717 The attachments folder appears once the broker has restarted with attachments on. Press Refresh status." &&
                revealed == readme,
                "ATTACHMENTS_REVEAL_FILE: the action reveals the README file inside the module's storage, and says exactly why when it cannot");        }
        finally
        {
            module.Shutdown(); Pump(() => module.pending.IsCompleted, dispatcher);
            try { if (Directory.Exists(storage)) Directory.Delete(storage, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
