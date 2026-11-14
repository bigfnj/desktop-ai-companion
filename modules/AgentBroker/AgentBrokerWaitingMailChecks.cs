using System.Diagnostics;
using System.Text;
using AgentBroker.Protocol;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.AgentBroker;

// BSM-4 (1.10.5) and FU-1: the waiting-mail Info row and the delivery-state wording, through the real label function and the
// real module Init, poll loop and pane with a synthetic supervisor (no worker child). Each label below has a killing mutation
// in tests/mutate-selftest-guards.py ("broker-pane: waiting: ..." and "broker-pane: wording: ...").
public sealed partial class AgentBrokerModule
{
    private sealed class MailSupervisor(BrokerSnapshot view) : IWorkerSupervisor
    {
        internal volatile ControlResponse? Next;
        internal volatile Exception? Fault;
        // Answered only when the check completes it, whatever the token says: a late answer.
        internal TaskCompletionSource<ControlResponse?>? Gate;
        internal int Reads;
        public Task<WorkerStatus> RefreshAsync(WorkerLaunch launch, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(new WorkerStatus(true, true, "running", view)); }
        public Task StopOwnedAsync(string directory, CancellationToken token) => Task.CompletedTask;
        public Task<ControlResponse?> ReadWaitingMailAsync(WorkerLaunch launch, CancellationToken token)
        {
            TaskCompletionSource<ControlResponse?>? gate = Interlocked.Exchange(ref Gate, null);
            Interlocked.Increment(ref Reads);
            if (gate is not null) return gate.Task;
            if (Fault is { } fault) return Task.FromException<ControlResponse?>(fault);
            return Task.FromResult(Next);
        }
    }

    // Like Pump, but a condition that never holds is a false result for the check to report by its label, not an exception.
    private static bool Settle(Func<bool> done, TestDispatcher dispatcher, int milliseconds = 5000)
    {
        var watch = Stopwatch.StartNew();
        while (!done() && watch.ElapsedMilliseconds < milliseconds) { dispatcher.Drain(); Thread.Sleep(5); }
        dispatcher.Drain();
        return done();
    }

    private static (BrokerSnapshot Status, SessionView Alpha, SessionView Beta, SessionView Zeta) WaitingFixture()
    {
        var alpha = new SessionView("claude:" + Guid.NewGuid().ToString("D"), Guid.NewGuid(), "Alpha", "claude-native", "Busy", true, true, false, false);
        var beta = new SessionView("codex:" + Guid.NewGuid().ToString("D"), Guid.NewGuid(), "Beta", "codex-native", "Unknown", true, true, false, false);
        var zeta = new SessionView("mcp:" + Guid.NewGuid().ToString("D"), Guid.NewGuid(), "Zeta", "mcp", "Idle", true, false, false, false);
        var status = new BrokerSnapshot(Guid.NewGuid(), "Synthetic", "http://127.0.0.1:7432", null, [], [], [], [alpha, beta, zeta],
            new Dictionary<string, string>());
        return (status, alpha, beta, zeta);
    }

    private static WaitingMailSessionView MailRow(SessionView session, int pending, int queued = 0, int busy = 0, long? ageMs = null,
        DateTimeOffset? at = null) => new(session.SessionId, session.Incarnation, pending, queued, busy, busy, 0, at, ageMs);

    private static ControlResponse MailAnswer(Guid brokerId, int noticeAfterSeconds, params WaitingMailSessionView[] rows) =>
        new(true, "ok", WaitingMail: new(brokerId, DateTimeOffset.UtcNow, noticeAfterSeconds, rows));

    private static void RunWaitingMailChecks(Action<bool, string> check, TestDispatcher dispatcher)
    {
        RunWaitingMailLabelChecks(check);
        RunWaitingMailPollChecks(check, dispatcher);
        // Before the stale checks, so the retargeted WAITING_PASSIVE there is never the first failure of a signal mutant.
        RunWaitingMailSignalChecks(check, dispatcher);
        RunWaitingMailStaleChecks(check, dispatcher);
        RunWaitingMailLoopFailureCheck(check, dispatcher);
        // After the stale and loop checks, so their own clearing mutants still fail first at their own labels.
        RunWaitingMailSignalLifecycleChecks(check, dispatcher);
        RunDeliveryWordingChecks(check, dispatcher);
    }

    // The passive waiting-mail signals (PLAN 3.6 under Addendum A.3), read through their real consumers: the Status row from the
    // pane's Load and the tray root's DynamicText. Literal texts, so a wording change fails here and not only in the label.
    private const string WaitingStatusSuffix = " Mail has waited past the notice threshold for a busy session; see Waiting mail.";
    private const string WaitingTrayLabel = "AgentBroker: mail waiting for a busy session";

    private static void RunWaitingMailSignalChecks(Action<bool, string> check, TestDispatcher dispatcher)
    {
        var (status, alpha, beta, _) = WaitingFixture();
        Guid id = status.BrokerId;
        // A Codex row reading Unknown, joined to a worker row whose oldest busy message is past the 300 s threshold.
        WaitingMailSessionView betaPast = MailRow(beta, 1, 1, 1, 420_000);
        ControlResponse eligible = MailAnswer(id, 300, betaPast);
        ControlResponse quiet = MailAnswer(id, 300, MailRow(beta, 1, 1));

        string[] texts = [WaitingMailStatusSignal(1), WaitingMailStatusSignal(2), WaitingMailTrayLabel("AgentBroker", 1),
            WaitingMailTrayLabel("AgentBroker", 2), WaitingMailTrayLabel(TrayRootWaitingLabel, 1)];
        string[] claims = ["sent", "notif", "stuck", "deliver", "told", "alert"];
        check(texts[0] == WaitingStatusSuffix && texts[1] == " Mail has waited past the notice threshold for 2 busy sessions; see Waiting mail." &&
            texts[2] == WaitingTrayLabel && texts[3] == "AgentBroker: mail waiting for 2 busy sessions" &&
            texts[4] == "AgentBroker: pairing request waiting; mail waiting for a busy session" &&
            WaitingMailStatusSignal(0) == "" && WaitingMailTrayLabel("AgentBroker", 0) == "AgentBroker" &&
            texts.All(text => text.All(ch => ch < 128) && !claims.Any(claim => text.Contains(claim, StringComparison.OrdinalIgnoreCase))),
            "WAITING_SIGNAL_WORDING: the signals say mail has waited past the notice threshold for a busy session, never that a notice was sent, work is stuck or mail was delivered");

        check(WaitingMailSignalSessions(true, false, status, eligible) == 1 && WaitingMailSignalSessions(false, false, status, eligible) == 0 &&
            WaitingMailSignalSessions(true, true, status, eligible) == 0 && WaitingMailSignalSessions(true, false, null, eligible) == 0,
            "WAITING_SIGNAL_GUARDS: the shared eligibility needs an enabled module that is not shut down and a current status, even with an eligible answer");

        var host = new TestHost();
        host.UseStorage(Id, new TestStorage(Path.Combine(Path.GetTempPath(), "agentbroker-waiting-signal-synthetic")));
        host.SettingsFor(Id).Set("enabled", "true");
        var supervisor = new MailSupervisor(status) { Next = quiet };
        var module = new AgentBrokerModule(new StatusControl(), supervisor) { ClientSetup = new(new TestClientCommand()) };
        try
        {
            module.Init(host);
            OptionsPane pane = host.OptionsPanes.Single();
            // One status and answer applied the way a poll applies them, then both consumers read. Nothing drains the dispatcher
            // between the apply and the reads, so the background poll cannot interleave.
            (string Status, string Tray) Shown(BrokerSnapshot view, ControlResponse? answer)
            {
                module.ApplyStatus(new WorkerStatus(true, true, "running", view), module.NextObservation(), answer);
                return (pane.Load()["status"], host.TrayItems.Single().DynamicText?.Invoke() ?? "");
            }

            var baseline = Shown(status, quiet);
            var raised = Shown(status, eligible);
            check(beta.Activity == "Unknown" && baseline.Status.StartsWith("Running. 3 sessions, 0 paired brokers.", StringComparison.Ordinal) &&
                !baseline.Status.Contains("Mail has waited", StringComparison.Ordinal) && raised.Status == baseline.Status + WaitingStatusSuffix,
                "WAITING_STATUS_SIGNAL: a Codex row reading Unknown whose worker row has mail waiting while busy past the threshold adds the Status suffix after the core status");
            check(baseline.Tray == "AgentBroker" && raised.Tray == WaitingTrayLabel,
                "WAITING_TRAY_SIGNAL: the same state labels the tray root AgentBroker: mail waiting for a busy session, and it reads AgentBroker without it");
            check(host.Asks.Count == 0 && host.SaidLines.Count == 0 && host.BroadcastLines.Count == 0 && host.PlayedSounds.Count == 0,
                "WAITING_PASSIVE: the Status and tray signals raise no question, speech, broadcast or sound");

            // The worker's own threshold: 150,000 ms is past 120 s and short of 300 s, so only the threshold decides.
            var at120 = Shown(status, MailAnswer(id, 120, MailRow(beta, 1, 1, 1, 150_000)));
            var at300 = Shown(status, MailAnswer(id, 300, MailRow(beta, 1, 1, 1, 150_000)));
            var at300Edge = Shown(status, MailAnswer(id, 300, MailRow(beta, 1, 1, 1, 120_000)));
            check(at120 == raised && at300 == baseline && at300Edge == baseline,
                "WAITING_SIGNAL_THRESHOLD_FROM_WORKER: a 120 s answer raises both signals at 150,000 ms; a 300 s answer raises neither at 150,000 or 120,000 ms");
            var below = Shown(status, MailAnswer(id, 120, MailRow(beta, 1, 1, 1, 119_999)));
            var exactly = Shown(status, MailAnswer(id, 120, MailRow(beta, 1, 1, 1, 120_000)));
            check(below == baseline && exactly == raised,
                "WAITING_SIGNAL_BOUNDARY: with a 120 s worker threshold, 119,999 ms raises neither signal and exactly 120,000 ms raises both");
            // The worker's monotonic age decides, never OldestBusyObservedAtUtc against this machine's clock: a timestamp three
            // hours old under the threshold age stays quiet, and one five hours ahead at the threshold age still raises both.
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var oldStamp = Shown(status, MailAnswer(id, 120, MailRow(beta, 1, 1, 1, 119_999, now.AddHours(-3))));
            var futureStamp = Shown(status, MailAnswer(id, 120, MailRow(beta, 1, 1, 1, 120_000, now.AddHours(5))));
            check(oldStamp == baseline && futureStamp == raised,
                "WAITING_SIGNAL_AGE_MONOTONIC: the signals use the worker's busy age; a busy-since time read against this clock never raises or clears them");

            // Sessions, never messages: the age is the oldest qualifying message's only.
            var two = Shown(status, MailAnswer(id, 300, MailRow(alpha, 2, 2, 2, 400_000), betaPast));
            var oneOfThree = Shown(status, MailAnswer(id, 300, MailRow(beta, 3, 3, 3, 420_000)));
            check(two.Status == baseline.Status + " Mail has waited past the notice threshold for 2 busy sessions; see Waiting mail." &&
                two.Tray == "AgentBroker: mail waiting for 2 busy sessions" && oneOfThree == raised,
                "WAITING_SIGNAL_SESSIONS: the signals count sessions: two eligible sessions read 2, one session with three busy messages reads one");

            // An incoming pairing offer at the same time: both signals and the core status stay.
            var peer = new PeerView(Guid.NewGuid(), "Pairing peer", "https://192.0.2.41:7433/", new string('A', 64));
            BrokerSnapshot offering = status with { PairingOffers = [new PairingView(Guid.NewGuid(), peer, "01234567 89ABCDEF FEDCBA98 76543210",
                DateTimeOffset.UtcNow.AddMinutes(10), false)] };
            var offerOnly = Shown(offering, quiet);
            string pairingRow = pane.Load()["pairingStatus"];
            var both = Shown(offering, eligible);
            string pairingBoth = pane.Load()["pairingStatus"];
            check(offerOnly.Status.StartsWith(baseline.Status, StringComparison.Ordinal) &&
                offerOnly.Status.EndsWith(" Pairing request waiting; see Pairing.", StringComparison.Ordinal) &&
                both.Status == offerOnly.Status + WaitingStatusSuffix && offerOnly.Tray == TrayRootWaitingLabel &&
                both.Tray == "AgentBroker: pairing request waiting; mail waiting for a busy session" &&
                pairingBoth == pairingRow && pairingBoth.StartsWith("Pairing request waiting from Pairing peer (", StringComparison.Ordinal),
                "WAITING_SIGNAL_WITH_PAIRING: with an incoming pairing offer, Status keeps the core status and the pairing text before the mail suffix, and the tray root carries both signals");

            // A pairing error and dropped notices at the same time: the suffix sits after the core status and before both, and
            // replaces neither. Reset afterwards so the later checks compare against the same baseline.
            BrokerSnapshot dropping = status with { DroppedNotices = new NoticeDropView(1, 0, 0, null) };
            module.SetPairingStatus("Synthetic pairing failure.", true);
            var errorsQuiet = Shown(dropping, quiet);
            var errorsRaised = Shown(dropping, eligible);
            string dropText = NoticeDropText(dropping.DroppedNotices);
            string errorsTail = " Synthetic pairing failure." + dropText;
            check(dropText.Length > 0 && errorsQuiet.Status == baseline.Status + errorsTail &&
                errorsRaised.Status == baseline.Status + WaitingStatusSuffix + errorsTail &&
                errorsQuiet.Tray == "AgentBroker" && errorsRaised.Tray == WaitingTrayLabel,
                "WAITING_SIGNAL_KEEPS_ERRORS: with a pairing error and dropped notices, Status keeps both after the mail suffix and the tray root still carries the signal");
            module.ResetPairingStatus();
            _ = Shown(status, quiet);

            // A waiting-only change notifies the open pane once, through InfoValues, and the Status it reloads carries the suffix.
            _ = Shown(status, quiet);
            var before = pane.Load();
            int notified = 0;
            string noticed = "";
            Action counter = () => { notified++; noticed = pane.Load()["status"]; };
            pane.InfoChanged += counter;
            _ = Shown(status, eligible);
            pane.InfoChanged -= counter;
            var after = pane.Load();
            string[] others = before.Keys.Where(key => key is not "status" and not "waitingMail" &&
                (!after.TryGetValue(key, out string? value) || value != before[key])).ToArray();
            check(notified == 1 && noticed == baseline.Status + WaitingStatusSuffix && others.Length == 0 && after["waitingMail"] != before["waitingMail"],
                "WAITING_SIGNAL_LIVE: a waiting-only change notifies the open pane once, and the Status it reloads carries the suffix" +
                (others.Length == 0 ? "" : " (also changed: " + string.Join(", ", others) + ")"));

            // Otherwise matching answers with exactly one identity field changed.
            ControlResponse foreign = eligible with { WaitingMail = eligible.WaitingMail! with { BrokerId = Guid.NewGuid() } };
            var matched = Shown(status, eligible);
            var mismatched = Shown(status, foreign);
            check(matched == raised && mismatched == baseline,
                "WAITING_SIGNAL_BROKER_MISMATCH: an otherwise eligible answer whose BrokerId is not the status snapshot's raises neither signal");
            var rejoined = Shown(status, eligible);
            var newIncarnation = Shown(status, MailAnswer(id, 300, betaPast with { Incarnation = Guid.NewGuid() }));
            check(rejoined == raised && newIncarnation == baseline,
                "WAITING_SIGNAL_EXACT_JOIN: the same SessionId under a new incarnation raises neither signal");
            var rejoinedAgain = Shown(status, eligible);
            var otherSession = Shown(status, MailAnswer(id, 300, betaPast with { SessionId = "codex:" + Guid.NewGuid().ToString("D") }));
            check(rejoinedAgain == raised && otherSession == baseline,
                "WAITING_SIGNAL_EXACT_JOIN: another SessionId under the same incarnation raises neither signal");

            // Each clearing answer follows a raised state.
            (string Status, string Tray) Cleared(ControlResponse? answer) { _ = Shown(status, eligible); return Shown(status, answer); }
            var nonePending = Cleared(MailAnswer(id, 300, betaPast with { PendingCount = 0, QueuedCount = 0 }));
            var allZero = Cleared(MailAnswer(id, 300, MailRow(alpha, 0), MailRow(beta, 0)));
            check(nonePending == baseline && allZero == baseline,
                "WAITING_SIGNAL_CLEARS: nothing pending clears both signals, even for a row that still carries a busy count and age");
            var submittedOnly = Cleared(MailAnswer(id, 300, MailRow(beta, 2)));
            var notBusy = Cleared(MailAnswer(id, 300, betaPast with { BlockedCount = 0, BusyCount = 0 }));
            var noAge = Cleared(MailAnswer(id, 300, betaPast with { OldestBusyObservedAgeMilliseconds = null }));
            var negativeAge = Cleared(MailAnswer(id, 300, betaPast with { OldestBusyObservedAgeMilliseconds = -1 }));
            check(submittedOnly == baseline && notBusy == baseline && noAge == baseline && negativeAge == baseline,
                "WAITING_SIGNAL_CLEARS: mail not waiting while busy (Submitted only, no busy count, no or a negative busy age) clears both signals");
            var unusable = new (string Name, ControlResponse? Response)[]
            {
                ("unknown_control_operation", new(false, "unknown_control_operation")),
                ("no answer", null),
                ("malformed payload", new(true, "ok", WaitingMail: new(id, DateTimeOffset.UtcNow, 300, null!))),
                ("zero threshold", MailAnswer(id, 0, betaPast)),
                ("success with another code", eligible with { Code = "accepted" }),
                ("refused with a payload", eligible with { Success = false, Code = "refused" })
            };
            // An answer the helper cannot read clears both signals and never throws: a throw is recorded under this label rather
            // than ending the self-test. The pure helper is asked first, then the pane and the tray.
            string Kept((string Name, ControlResponse? Response) item)
            {
                try
                {
                    if (WaitingMailSignalSessions(true, false, status, item.Response) != 0) return item.Name + " (helper)";
                    return Cleared(item.Response) != baseline ? item.Name : "";
                }
                catch (Exception error) { return item.Name + " (threw " + error.GetType().Name + ")"; }
            }
            string[] kept = unusable.Select(Kept).Where(name => name.Length > 0).ToArray();
            check(kept.Length == 0,
                "WAITING_SIGNAL_CLEARS: an older worker's unknown_control_operation, no answer, and a malformed, zero-threshold or unsuccessful answer clear both signals" +
                (kept.Length == 0 ? "" : " (kept: " + string.Join(", ", kept) + ")"));
        }
        finally
        {
            module.Shutdown();
            Pump(() => module.pending.IsCompleted, dispatcher);
        }
    }

    // Lifecycle clearing through the real poll: lost status, restart, disabling and shutdown each clear both signals, and a
    // status applied afterwards never re-joins the earlier answer.
    private static void RunWaitingMailSignalLifecycleChecks(Action<bool, string> check, TestDispatcher dispatcher)
    {
        var (status, _, beta, _) = WaitingFixture();
        var host = new TestHost();
        host.UseStorage(Id, new TestStorage(Path.Combine(Path.GetTempPath(), "agentbroker-waiting-signal-lifecycle-synthetic")));
        host.SettingsFor(Id).Set("enabled", "true");
        ControlResponse eligible = MailAnswer(status.BrokerId, 300, MailRow(beta, 1, 1, 1, 420_000));
        var supervisor = new MailSupervisor(status) { Next = eligible };
        var module = new AgentBrokerModule(new StatusControl(), supervisor) { ClientSetup = new(new TestClientCommand()) };
        var running = new WorkerStatus(true, true, "running", status);
        try
        {
            module.Init(host);
            OptionsPane pane = host.OptionsPanes.Single();
            string Tray() => host.TrayItems.Single().DynamicText?.Invoke() ?? "";
            bool Raised() => pane.Load()["status"].EndsWith(WaitingStatusSuffix, StringComparison.Ordinal) && Tray() == WaitingTrayLabel;
            bool Clear() => !pane.Load()["status"].Contains("Mail has waited", StringComparison.Ordinal) && Tray() == "AgentBroker";

            bool polled = Settle(Raised, dispatcher);
            check(polled, "WAITING_SIGNAL_POLLED: a connected status poll whose waiting mail is past the threshold raises both signals");

            // A poll that loses the worker (worker_exited and every other state without a snapshot) clears both signals, and the
            // next action's status, which keeps the held answer, never re-joins the answer from before the outage.
            module.ApplyStatus(new WorkerStatus(false, true, "worker_exited"), module.NextObservation(), null);
            bool exitedClear = Clear();
            module.ApplyStatus(running);
            bool exitedRejoin = Clear();
            bool resumed = Settle(Raised, dispatcher);
            check(polled && exitedClear && exitedRejoin && resumed,
                "WAITING_SIGNAL_LIFECYCLE_CLEARS: a poll that loses the worker clears both signals, a later action's status never re-joins the earlier answer, and the next connected poll raises them again");

            // The control fake answers status with a failure.
            string refreshed = module.RefreshStatusAsync().GetAwaiter().GetResult();
            bool lostClear = Clear();
            module.ApplyStatus(running);
            bool lostRejoin = Clear();
            check(polled && refreshed.StartsWith("The broker is unavailable", StringComparison.Ordinal) && lostClear && lostRejoin,
                "WAITING_SIGNAL_LIFECYCLE_CLEARS: a lost status clears both signals, and a later action's status never re-joins the earlier answer");

            bool back = Settle(Raised, dispatcher);
            bool saved = pane.Save(new Dictionary<string, string> { ["name"] = "Synthetic signal restart" });
            bool restartClear = Clear();
            module.ApplyStatus(running);
            bool restartRejoin = Clear();
            check(back && saved && restartClear && restartRejoin,
                "WAITING_SIGNAL_LIFECYCLE_CLEARS: a restart clears both signals, and a status read before the next poll never re-joins the earlier answer");

            bool again = Settle(Raised, dispatcher);
            bool disabled = pane.Save(new Dictionary<string, string> { ["enabled"] = "false" });
            bool disabledClear = Clear();
            // Even a status and eligible answer applied while disabled raise nothing.
            module.ApplyStatus(running, module.NextObservation(), eligible);
            bool disabledForced = Clear();
            check(again && disabled && disabledClear && disabledForced,
                "WAITING_SIGNAL_LIFECYCLE_CLEARS: disabling AgentBroker clears both signals, even against an eligible answer");

            bool enabled = pane.Save(new Dictionary<string, string> { ["enabled"] = "true" });
            bool raisedAgain = Settle(Raised, dispatcher);
            module.Shutdown();
            bool shutClear = Clear();
            module.ApplyStatus(running, module.NextObservation(), eligible);
            bool shutForced = Clear();
            check(enabled && raisedAgain && shutClear && shutForced,
                "WAITING_SIGNAL_LIFECYCLE_CLEARS: shutdown clears both signals, even against an eligible answer applied afterwards");
        }
        finally
        {
            module.Shutdown();
            Pump(() => module.pending.IsCompleted, dispatcher);
        }
    }

    private static void RunWaitingMailLabelChecks(Action<bool, string> check)
    {
        var (status, alpha, beta, zeta) = WaitingFixture();
        Guid id = status.BrokerId;

        var failed = new (string Name, ControlResponse? Response)[]
        {
            ("unknown_control_operation", new(false, "unknown_control_operation")),
            ("control_request_failed", new(false, "control_request_failed")),
            ("success without payload", new(true, "ok")),
            // The contract's success is Success true AND Code "ok"; any other code with a payload is not a measurement.
            ("success with another code", new(true, "accepted", WaitingMail: new(id, DateTimeOffset.UtcNow, 300, [MailRow(alpha, 2, 2)]))),
            ("no answer", null),
            ("refused with a payload", new(false, "refused", WaitingMail: new(id, DateTimeOffset.UtcNow, 300, [MailRow(alpha, 2, 2)]))),
            ("malformed payload", new(true, "ok", WaitingMail: new(id, DateTimeOffset.UtcNow, 300, null!))),
            ("zero threshold", new(true, "ok", WaitingMail: new(id, DateTimeOffset.UtcNow, 0, [MailRow(alpha, 2, 2)])))
        };
        string[] wrong = failed.Where(item => FormatWaitingMail(status, item.Response) != WaitingMailUnavailable).Select(item => item.Name).ToArray();
        check(wrong.Length == 0 && FormatWaitingMail(status, MailAnswer(id, 300)) == "No messages waiting for registered sessions." &&
            WaitingMailUnavailable.StartsWith("Measurement unavailable", StringComparison.Ordinal) &&
            !WaitingMailUnavailable.Any(char.IsDigit) && !WaitingMailUnavailable.Contains("No messages", StringComparison.Ordinal) &&
            !WaitingMailUnavailable.Contains("version", StringComparison.OrdinalIgnoreCase) &&
            !WaitingMailUnavailable.Contains("older", StringComparison.OrdinalIgnoreCase),
            "WAITING_UNAVAILABLE: unknown_control_operation, any other failure and a malformed answer read as measurement unavailable, never zero or an older-version claim" +
            (wrong.Length == 0 ? "" : " (not unavailable: " + string.Join(", ", wrong) + ")"));

        ControlResponse matching = MailAnswer(id, 300, MailRow(alpha, 3, 3, 1, 420_000));
        ControlResponse foreign = matching with { WaitingMail = matching.WaitingMail! with { BrokerId = Guid.NewGuid() } };
        check(FormatWaitingMail(status, matching).Contains("\"Alpha\"", StringComparison.Ordinal) &&
            FormatWaitingMail(status, foreign) == WaitingMailUnavailable,
            "WAITING_BROKER_MISMATCH: an answer whose BrokerId is not the status snapshot's is discarded whole, even when its addresses match");

        var sameIdOtherIncarnation = new WaitingMailSessionView(alpha.SessionId, Guid.NewGuid(), 4, 4, 0, 0, 0, null, null);
        string joined = FormatWaitingMail(status, MailAnswer(id, 300, MailRow(alpha, 0), sameIdOtherIncarnation));
        check(!joined.Contains("Alpha", StringComparison.Ordinal) && joined != WaitingMailNone &&
            joined.StartsWith("4 pending for 1 address not in the current session list.", StringComparison.Ordinal),
            "WAITING_EXACT_JOIN: a row for the same SessionId under another incarnation never attaches to that session, and its mail still counts");

        string none = FormatWaitingMail(status, MailAnswer(id, 300, MailRow(alpha, 0), MailRow(beta, 0), MailRow(zeta, 0)));
        string one = FormatWaitingMail(status, MailAnswer(id, 300, MailRow(alpha, 0), MailRow(beta, 2, 2), MailRow(zeta, 0)));
        check(none == "No messages waiting for registered sessions." && one.StartsWith("\"Beta\" (codex-native): 2 pending, 2 still queued.", StringComparison.Ordinal) &&
            !one.Contains("Alpha", StringComparison.Ordinal) && !one.Contains("Zeta", StringComparison.Ordinal),
            "WAITING_ROWS_FILTERED: sessions with nothing pending are not shown, and all-zero rows read No messages waiting");

        // The worker reports a row per registered session only, so "nothing pending" is never a claim about every address.
        check(FormatWaitingMail(status, MailAnswer(id, 300, MailRow(alpha, 0), MailRow(beta, 0))) == "No messages waiting for registered sessions.",
            "WAITING_NONE_SCOPED: an answer with nothing pending says no messages wait for registered sessions, never that no mail waits at all");

        // A Codex row whose four counts all differ (pending 4, queued 3, blocked 2, busy 1): each count is shown as the worker
        // counted it, and Accepted mail is "still queued", never "not yet delivered" (pending also holds Submitted mail, which
        // is not Delivered either).
        string distinct = FormatWaitingMail(status, MailAnswer(id, 300, new WaitingMailSessionView(beta.SessionId, beta.Incarnation, 4, 3, 2, 1, 0, null, 420_000)));
        check(distinct == "\"Beta\" (codex-native): 4 pending, 3 still queued, 1 waiting while it is busy, observed busy for at least 7 min, " +
                "past the 5 min sender notice threshold. Busy time is a process-local observed lower bound; resets on worker restart." &&
            !distinct.Contains("deliver", StringComparison.OrdinalIgnoreCase),
            "WAITING_COUNTS_DISTINCT: pending, queued and busy are each shown as the worker counted them (4, 3 and 1), and queued mail is never called delivered");

        string at120 = FormatWaitingMail(status, MailAnswer(id, 120, MailRow(alpha, 1, 1, 1, 150_000)));
        string at300 = FormatWaitingMail(status, MailAnswer(id, 300, MailRow(alpha, 1, 1, 1, 150_000)));
        check(at120.Contains("observed busy for at least 2 min, past the 2 min sender notice threshold", StringComparison.Ordinal) &&
            at300.Contains("observed busy for at least 2 min", StringComparison.Ordinal) && !at300.Contains("notice threshold", StringComparison.Ordinal),
            "WAITING_THRESHOLD_FROM_WORKER: the notice threshold is the answer's NoticeAfterSeconds (120 s, reached at 150 s), never a second 300");

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string skewedPast = FormatWaitingMail(status, MailAnswer(id, 300, MailRow(alpha, 1, 1, 1, 420_000, now.AddHours(-3))));
        string skewedFuture = FormatWaitingMail(status, MailAnswer(id, 300, MailRow(alpha, 1, 1, 1, 420_000, now.AddHours(5))));
        string untimed = FormatWaitingMail(status, MailAnswer(id, 300, MailRow(alpha, 1, 1, 1, 420_000)));
        check(skewedPast.Contains("observed busy for at least 7 min, past the 5 min sender notice threshold", StringComparison.Ordinal) &&
            skewedPast == skewedFuture && skewedPast == untimed,
            "WAITING_AGE_MONOTONIC: the busy age is the worker's OldestBusyObservedAgeMilliseconds; OldestBusyObservedAtUtc against this clock is ignored");

        check(untimed.EndsWith(". Busy time is a process-local observed lower bound; resets on worker restart.", StringComparison.Ordinal) &&
            !untimed.Contains("since worker start", StringComparison.OrdinalIgnoreCase),
            "WAITING_LOWER_BOUND_WORDING: the row says the busy time is a process-local observed lower bound that resets on worker restart, never since worker start");

        SessionView[] many = Enumerable.Range(0, 10).Select(index => new SessionView("mcp:many-" + index, Guid.NewGuid(), "Many " + index, "mcp", "Idle",
            true, false, false, false)).ToArray();
        string bounded = FormatWaitingMail(status with { Sessions = many },
            MailAnswer(id, 3600, many.Select((session, index) => MailRow(session, 1, 1, 1, (index + 1) * 60_000L)).ToArray()));
        check(bounded.StartsWith("\"Many 9\" (mcp): 1 pending", StringComparison.Ordinal) && bounded.Contains("\"Many 2\"", StringComparison.Ordinal) &&
            !bounded.Contains("\"Many 1\"", StringComparison.Ordinal) && !bounded.Contains("\"Many 0\"", StringComparison.Ordinal) &&
            bounded.Contains("; and 2 more sessions. ", StringComparison.Ordinal),
            "WAITING_ROW_BOUNDED: at most eight sessions, the longest observed busy first, then how many more");

        SessionView[] nine = many.Take(9).ToArray();
        string single = FormatWaitingMail(status with { Sessions = nine },
            MailAnswer(id, 3600, nine.Select((session, index) => MailRow(session, 1, 1, 1, (index + 1) * 60_000L)).ToArray()));
        check(single.Contains("; and 1 more session. ", StringComparison.Ordinal) && bounded.Contains("; and 2 more sessions. ", StringComparison.Ordinal),
            "WAITING_MORE_SINGULAR: nine sessions end with and 1 more session, ten with and 2 more sessions");
    }

    private static void RunWaitingMailPollChecks(Action<bool, string> check, TestDispatcher dispatcher)
    {
        var (status, alpha, _, _) = WaitingFixture();
        var host = new TestHost();
        host.UseStorage(Id, new TestStorage(Path.Combine(Path.GetTempPath(), "agentbroker-waiting-mail-synthetic")));
        host.SettingsFor(Id).Set("enabled", "true");
        ControlResponse alphaMail = MailAnswer(status.BrokerId, 300, MailRow(alpha, 2, 2));
        var supervisor = new MailSupervisor(status) { Next = alphaMail };
        var module = new AgentBrokerModule(new StatusControl(), supervisor) { ClientSetup = new(new TestClientCommand()) };
        bool ShowsAlpha(OptionsPane pane) => pane.Load()["waitingMail"].StartsWith("\"Alpha\" (claude-native): 2 pending", StringComparison.Ordinal);
        static string[] Changed(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after) =>
            before.Keys.Where(key => key != "waitingMail" && (!after.TryGetValue(key, out string? value) || value != before[key])).ToArray();
        try
        {
            module.Init(host);
            OptionsPane pane = host.OptionsPanes.Single();
            bool polled = Settle(() => ShowsAlpha(pane), dispatcher);
            check(polled && pane.Schema.Any(field => field is { Id: "waitingMail", Kind: SettingKind.Info, Group: "AgentBroker", Label: "Waiting mail" }),
                "WAITING_POLLED: a connected status poll reads waiting mail and the AgentBroker group's Waiting mail row shows it");
            var available = pane.Load();

            supervisor.Next = new(false, "unknown_control_operation");
            bool older = Settle(() => pane.Load()["waitingMail"] == WaitingMailUnavailable, dispatcher);
            string[] changed = Changed(available, pane.Load());
            check(older && changed.Length == 0 && pane.Load()["status"].StartsWith("Running", StringComparison.Ordinal),
                "WAITING_PANE_INTACT: an older worker's unknown_control_operation changes only the Waiting mail row" +
                (changed.Length == 0 ? "" : " (changed: " + string.Join(", ", changed) + ")"));

            supervisor.Next = alphaMail;
            bool back = Settle(() => ShowsAlpha(pane), dispatcher);
            supervisor.Fault = new IOException("Synthetic waiting-mail transport failure.");
            bool faulted = Settle(() => pane.Load()["waitingMail"] == WaitingMailUnavailable, dispatcher);
            changed = Changed(available, pane.Load());
            check(back && faulted && changed.Length == 0 && pane.Load()["status"].StartsWith("Running", StringComparison.Ordinal),
                "WAITING_PANE_INTACT: a waiting-mail read that throws is measurement unavailable for that poll, and supervision and the rest of the pane carry on" +
                (changed.Length == 0 ? "" : " (changed: " + string.Join(", ", changed) + ")"));
        }
        finally
        {
            module.Shutdown();
            Pump(() => module.pending.IsCompleted, dispatcher);
        }
    }

    private static void RunWaitingMailStaleChecks(Action<bool, string> check, TestDispatcher dispatcher)
    {
        var (status, alpha, beta, _) = WaitingFixture();
        var host = new TestHost();
        host.UseStorage(Id, new TestStorage(Path.Combine(Path.GetTempPath(), "agentbroker-waiting-stale-synthetic")));
        host.SettingsFor(Id).Set("enabled", "true");
        var late = new TaskCompletionSource<ControlResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var supervisor = new MailSupervisor(status) { Gate = late, Next = MailAnswer(status.BrokerId, 300) };
        var module = new AgentBrokerModule(new StatusControl(), supervisor) { ClientSetup = new(new TestClientCommand()) };
        var running = new WorkerStatus(true, true, "running", status);
        try
        {
            module.Init(host);
            OptionsPane pane = host.OptionsPanes.Single();
            var shown = new List<string>();
            pane.InfoChanged += () => shown.Add(pane.Load()["waitingMail"]);
            // The first poll's read is still out when the settings change restarts the worker; its answer arrives afterwards.
            bool entered = Settle(() => Volatile.Read(ref supervisor.Reads) > 0, dispatcher);
            bool restarted = pane.Save(new Dictionary<string, string> { ["name"] = "Synthetic restarted" });
            late.SetResult(MailAnswer(status.BrokerId, 300, MailRow(alpha, 9, 9)));
            bool settled = Settle(() => Volatile.Read(ref supervisor.Reads) > 1 && pane.Load()["waitingMail"] == WaitingMailNone, dispatcher);
            check(entered && restarted && settled && !shown.Any(text => text.Contains("Alpha", StringComparison.Ordinal)),
                "WAITING_STALE_LATE_ANSWER: a waiting-mail answer that arrives after a restart is discarded with its generation and never shown");

            // Mail held from before a restart must not join the first status read after it (Refresh status, before any poll).
            module.ApplyStatus(running, module.NextObservation(), MailAnswer(status.BrokerId, 300, MailRow(alpha, 2, 2)));
            string held = pane.Load()["waitingMail"];
            // A status from another source (Refresh status, an action's answer) keeps the held answer until the next poll.
            module.ApplyStatus(running);
            string kept = pane.Load()["waitingMail"];
            check(held.Contains("\"Alpha\"", StringComparison.Ordinal) && kept == held,
                "WAITING_HELD_ACROSS_STATUS: a status from another source (Refresh status, an action's answer) keeps the held waiting mail");
            bool saved = pane.Save(new Dictionary<string, string> { ["name"] = "Synthetic again" });
            module.ApplyStatus(running);
            string afterRestart = pane.Load()["waitingMail"];
            check(held.Contains("\"Alpha\"", StringComparison.Ordinal) && saved && afterRestart == WaitingMailUnavailable,
                "WAITING_STALE_RESTART_CLEARS: a restart drops the previous worker's waiting mail, so a status read before the next poll never shows it");

            int asks = host.Asks.Count;
            module.ApplyStatus(running, module.NextObservation(), MailAnswer(status.BrokerId, 120, MailRow(alpha, 3, 3, 3, 600_000)));
            string past = pane.Load()["waitingMail"];
            // Freeze 3 retargets the tray clause: the tray root used to stay exactly "AgentBroker"; PLAN 3.6 now gives it the
            // passive waiting-mail label, so the clause pins that exact label instead (still an exact equality).
            check(past.Contains("past the 2 min sender notice threshold", StringComparison.Ordinal) && asks == 0 && host.Asks.Count == 0 &&
                host.SaidLines.Count == 0 && host.BroadcastLines.Count == 0 && host.PlayedSounds.Count == 0 &&
                host.TrayItems.Single().DynamicText?.Invoke() == WaitingTrayLabel,
                "WAITING_PASSIVE: mail past the notice threshold raises no question, speech, broadcast or sound, and the tray root gains only its passive label");

            int notified = 0;
            pane.InfoChanged += () => notified++;
            module.ApplyStatus(running, module.NextObservation(), MailAnswer(status.BrokerId, 300, MailRow(alpha, 1, 1)));
            int before = notified;
            module.ApplyStatus(running, module.NextObservation(), MailAnswer(status.BrokerId, 300, MailRow(alpha, 1, 1), MailRow(beta, 2, 2)));
            check(notified == before + 1 && pane.Load()["waitingMail"].Contains("\"Beta\" (codex-native): 2 pending", StringComparison.Ordinal),
                "WAITING_INFO_LIVE: a change in waiting mail alone notifies the open pane, once");

            // A failed Refresh status leaves no status, so no measurement either: an action's status after it never re-joins the
            // answer held from before (the control fake answers status with a failure).
            string refreshed = module.RefreshStatusAsync().GetAwaiter().GetResult();
            string lost = pane.Load()["waitingMail"];
            module.ApplyStatus(running);
            string rejoined = pane.Load()["waitingMail"];
            check(refreshed.StartsWith("The broker is unavailable", StringComparison.Ordinal) && lost == "No worker status yet." &&
                rejoined == WaitingMailUnavailable,
                "WAITING_STATUS_LOST_CLEARS: a failed Refresh status drops the held waiting mail, so a later action's status never re-joins it" +
                (rejoined == WaitingMailUnavailable ? "" : " (shown: " + rejoined + ")"));
        }
        finally
        {
            late.TrySetResult(null);
            module.Shutdown();
            Pump(() => module.pending.IsCompleted, dispatcher);
        }
    }

    // The poll loop's own failure path (an exception outside its inner catches, here an approval handoff that throws
    // InvalidOperationException) ends polling with no status; the held answer must go with it.
    private static void RunWaitingMailLoopFailureCheck(Action<bool, string> check, TestDispatcher dispatcher)
    {
        var (fixture, alpha, _, _) = WaitingFixture();
        BrokerSnapshot status = fixture with { ApprovalPrompts = new("fresh", 1, DateTimeOffset.UtcNow) };
        var host = new TestHost();
        host.UseStorage(Id, new TestStorage(Path.Combine(Path.GetTempPath(), "agentbroker-waiting-loop-synthetic")));
        host.SettingsFor(Id).Set("enabled", "true");
        var control = new StatusControl();
        var supervisor = new MailSupervisor(status) { Next = MailAnswer(status.BrokerId, 300, MailRow(alpha, 2, 2)) };
        var module = new AgentBrokerModule(control, supervisor) { ClientSetup = new(new TestClientCommand()) };
        try
        {
            module.Init(host);
            OptionsPane pane = host.OptionsPanes.Single();
            bool shown = Settle(() => pane.Load()["waitingMail"].StartsWith("\"Alpha\"", StringComparison.Ordinal), dispatcher);
            control.Failure = new InvalidOperationException("Synthetic approval handoff failure.");
            bool ended = Settle(() => pane.Load()["status"].StartsWith("Worker unavailable", StringComparison.Ordinal), dispatcher);
            module.ApplyStatus(new WorkerStatus(true, true, "running", status));
            string rejoined = pane.Load()["waitingMail"];
            check(shown && ended && rejoined == WaitingMailUnavailable,
                "WAITING_STATUS_LOST_CLEARS: a poll loop that fails drops the held waiting mail, so a later action's status never re-joins it" +
                (rejoined == WaitingMailUnavailable ? "" : " (shown: " + rejoined + ")"));
        }
        finally
        {
            module.Shutdown();
            Pump(() => module.pending.IsCompleted, dispatcher);
        }
    }

    private static void RunDeliveryWordingChecks(Action<bool, string> check, TestDispatcher dispatcher)
    {
        var host = new TestHost();
        host.UseStorage(Id, new TestStorage(Path.Combine(Path.GetTempPath(), "agentbroker-wording-synthetic")));
        var module = new AgentBrokerModule(new StatusControl(), new TestSupervisor());
        try
        {
            module.Init(host);
            Pump(() => module.pending.IsCompleted, dispatcher);
            string help = host.OptionsPanes.Single().Load()["messageLifetimeHelp"];
            byte[]? bytes = GuideBytes();
            string guide = bytes is null ? "" : Encoding.UTF8.GetString(bytes);
            string[] meaning = ["Delivered remains Delivered after its original delivery deadline", "reading alone does not acknowledge",
                "Held keeps the text without extending its deadline"];
            string[] retired = ["until the recipient reads it", "remain received"];
            check(guide.Length > 0 && meaning.All(text => help.Contains(text, StringComparison.Ordinal) && guide.Contains(text, StringComparison.Ordinal)) &&
                retired.All(text => !help.Contains(text, StringComparison.OrdinalIgnoreCase) && !guide.Contains(text, StringComparison.OrdinalIgnoreCase)) &&
                // The whole sentence: a reply settles a parent only in narrow cases (PICK-C: local parent and reply, not Held,
                // replier with Receive), and never mail from a paired broker, so the guide makes no reply claim here.
                guide.Contains("stays pending in its recipient's inbox until the recipient acknowledges it or its deadline passes.", StringComparison.Ordinal),
                "WORDING_DELIVERY_STATES: the pane help and the guide say reading never acknowledges, Delivered stays Delivered and Held keeps its deadline");
            // FU-3: a client row is one summary line only without supplementary notes or a stored setup result.
            check(guide.Contains("supplementary notes or a stored setup result (Last setup action) add lines too, so a healthy client is a single line only when it has neither", StringComparison.Ordinal),
                "WORDING_CLIENT_LINES: the guide says notes and a stored setup result add lines, so healthy means one line only without them");
            // The compiled --watch-inbox recipe in the wake section (Codex's CLI contract 193B7065): a bounded parent walk that
            // prints the creation FILETIME, a runnable argument array with an explicit duration and seen IDs, the exit-code
            // meanings, and no qualification-only helper or prototype output names.
            int wakeStart = guide.IndexOf("<section id=\"wake\">", StringComparison.Ordinal);
            int wakeEnd = wakeStart < 0 ? -1 : guide.IndexOf("</section>", wakeStart, StringComparison.Ordinal);
            string wake = wakeStart < 0 || wakeEnd < 0 ? "" : guide[wakeStart..wakeEnd];
            string[] recipe = ["--watch-inbox", "Claude Code only", "StartTime.ToUniversalTime().ToFileTimeUtc()",
                "Do not use a .NET DateTime's Ticks", "-ne 'claude.exe' -and $i -lt 32", "throw 'No claude.exe ancestor",
                "'--max-seconds', '7200'", "'--seen-ids', '&lt;handled IDs", "&amp; $worker @watchArgs", "--unbound",
                "Arming never requires acknowledging anything", "they never mean mail", "handle each message ID once"];
            check(wake.Length > 0 && recipe.All(text => wake.Contains(text, StringComparison.Ordinal)) &&
                !guide.Contains("Get-ClaudeHost", StringComparison.Ordinal) && !guide.Contains("AGENTBROKER_MAIL", StringComparison.Ordinal),
                "WATCH_GUIDE_RECIPE: the wake section's --watch-inbox recipe has a bounded parent walk printing the FILETIME, an explicit duration, seen IDs, the exit meanings and no prototype helper");
            // Windows PowerShell 5.1 drops an empty native argument, so "--seen-ids ''" reaches the worker as a valueless flag
            // and the command is refused (exit 2). The recipe must say to leave a pair out, never to pass it empty (freeze 5).
            check(wake.Contains("Never pass an empty value: Windows PowerShell 5.1 drops an empty argument", StringComparison.Ordinal) &&
                wake.Contains("leave out the --cursor pair when you have no cursor and the --seen-ids pair when you handled no IDs", StringComparison.Ordinal) &&
                wake.Contains("If there are none, leave the whole --seen-ids pair out of the command.", StringComparison.Ordinal) &&
                !wake.Contains("or empty&gt;", StringComparison.Ordinal),
                "WATCH_GUIDE_EMPTY_ARGS: the recipe says to leave the --cursor and --seen-ids pairs out instead of passing an empty value");
            // A failed output write ends with exit 26 and may leave no complete line, so the line is promised only on a normal end.
            check(wake.Contains("When the command ends normally it prints one line of fixed metadata", StringComparison.Ordinal) &&
                wake.Contains("a run that ends without a complete line mean review the command; they never mean mail", StringComparison.Ordinal) &&
                !wake.Contains("it has printed one line", StringComparison.Ordinal),
                "WATCH_GUIDE_NO_LINE: the recipe promises the metadata line only on a normal end, and treats no complete line like exit 26, never as mail");
            // Freeze 7 (G7 claims audit wf_1c41a366-ff1, 2026-11-12): the recipe must run as written on a default Windows
            // install and must not double the session prefix. The default execution policy refuses an unsigned .ps1 under a
            // plain -File, and whoami's sessionId already carries "claude:" (InboxWatchClient.cs:43 refuses claude:claude:).
            check(wake.Contains("run it with <code>powershell.exe -NoProfile -ExecutionPolicy Bypass -File</code> on that file", StringComparison.Ordinal) &&
                wake.Contains("runs powershell.exe -NoProfile -ExecutionPolicy Bypass -File on that file", StringComparison.Ordinal) &&
                !wake.Contains("powershell.exe -NoProfile -File", StringComparison.Ordinal) &&
                wake.Contains("'--session-id', '&lt;sessionId from whoami, which already starts with claude:&gt;'", StringComparison.Ordinal) &&
                !wake.Contains("'claude:&lt;session ID&gt;'", StringComparison.Ordinal),
                "WATCH_GUIDE_RUNNABLE: the recipe runs its scripts with -ExecutionPolicy Bypass and takes the session ID from whoami as printed, never adding a second claude: prefix");

            int start = guide.IndexOf("<section id=\"waiting-mail\">", StringComparison.Ordinal);
            int end = start < 0 ? -1 : guide.IndexOf("</section>", start, StringComparison.Ordinal);
            string section = start < 0 || end < 0 ? "" : guide[start..end];
            check(section.Contains("<b>Waiting mail</b>", StringComparison.Ordinal) &&
                section.Contains("a process-local observed lower bound; resets on worker restart", StringComparison.Ordinal) &&
                section.Contains("\"Measurement unavailable\"", StringComparison.Ordinal) && section.Contains("It never means zero", StringComparison.Ordinal) &&
                section.Contains("no popup or question", StringComparison.Ordinal) && !guide.Contains("since worker start", StringComparison.OrdinalIgnoreCase),
                "WAITING_GUIDE_MATCHES: the guide's Waiting mail section describes the row: a lower bound, measurement unavailable never zero, passive");

            check(section.Contains("\"" + WaitingMailNone + "\"", StringComparison.Ordinal) && section.Contains("still queued (Accepted)", StringComparison.Ordinal) &&
                section.Contains("none of them is Delivered", StringComparison.Ordinal) && !section.Contains("not yet delivered", StringComparison.OrdinalIgnoreCase),
                "WAITING_GUIDE_COUNTS: the guide calls queued mail still queued (Accepted), never not yet delivered, and quotes the row's empty text");

            check(section.Contains("only to a sender on this workstation", StringComparison.Ordinal) &&
                section.Contains("a sender on a paired broker gets none", StringComparison.Ordinal) &&
                !section.Contains("the sender is told by the worker", StringComparison.OrdinalIgnoreCase),
                "WAITING_GUIDE_SENDERS: the guide says the worker's advisory goes only to a sender on this workstation, never to a paired broker's");

            check(section.Contains("The pane raises no popup or question. While mail for a registered session has waited past that threshold, Status and " +
                "the tray's AgentBroker label also say mail is waiting for a busy session, and both clear when that no longer holds.", StringComparison.Ordinal),
                "WAITING_GUIDE_SIGNAL: the guide says Status and the tray label also say mail is waiting for a busy session, and clear with it");
        }
        finally
        {
            module.Shutdown();
            Pump(() => module.pending.IsCompleted, dispatcher);
        }
    }
}
