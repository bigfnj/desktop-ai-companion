using System.Globalization;
using System.Text;
using AgentBroker.Protocol;

namespace DesktopAICompanion.AgentBroker;

// BSM-4 (1.10.5): the passive "Waiting mail" Info row (busy-mail worker contract v2, "Wire response and consumer join").
// Its data is the worker's separate "waiting-mail" control operation, read after each connected status in the same poll
// and applied in the same generation-guarded post as that status. The row is a pure function of the current status
// snapshot and the last answer: no popup, no question, no episode latch.
public sealed partial class AgentBrokerModule
{
    internal const string WaitingMailOperation = "waiting-mail";
    // A third outcome, never zero: a worker without the operation (unknown_control_operation), any other failure, no answer
    // and an answer for another broker identity all read the same. It makes no claim about the worker's version.
    internal const string WaitingMailUnavailable = "Measurement unavailable: the worker did not report waiting mail.";
    // The worker has one row per registered session only: mail kept for an address that is no longer registered (an exited
    // native incarnation, a mailbox lost to a restart) has no row, so the empty text is scoped to registered sessions.
    internal const string WaitingMailNone = "No messages waiting for registered sessions.";
    internal const string WaitingMailLowerBound = "Busy time is a process-local observed lower bound; resets on worker restart.";
    private const int WaitingMailShownSessions = 8;

    // The answer read with the status the module last applied, or null when none was read. Joined to the current snapshot
    // each time the row is drawn, so a later status from another broker identity discards it.
    private ControlResponse? waitingMail;

    private string WaitingMailText() => !settings.Enabled ? "Enable AgentBroker to see waiting mail." : FormatWaitingMail(snapshot, waitingMail);

    internal static string FormatWaitingMail(BrokerSnapshot? status, ControlResponse? response)
    {
        if (status is null) return "No worker status yet.";
        if (response is not { Success: true, Code: "ok", WaitingMail: { } view } || !WaitingMailUsable(view)) return WaitingMailUnavailable;
        // Status and waiting-mail are separate reads: an answer from another broker identity is discarded whole.
        if (view.BrokerId != status.BrokerId) return WaitingMailUnavailable;
        SessionView[] listed = status.Sessions.Where(session => session is not null).ToArray();
        var shown = new List<(SessionView Session, WaitingMailSessionView Row)>();
        int unlistedAddresses = 0;
        long unlistedPending = 0;
        // One row exists per registered session, most of them with nothing pending.
        foreach (WaitingMailSessionView row in view.Sessions.Where(row => row.PendingCount > 0))
        {
            // The exact address: the same SessionId under another incarnation is another mailbox and never borrows a label.
            SessionView? session = listed.FirstOrDefault(item => item.SessionId == row.SessionId && item.Incarnation == row.Incarnation);
            if (session is null) { unlistedAddresses++; unlistedPending += row.PendingCount; }
            else shown.Add((session, row));
        }
        if (shown.Count == 0 && unlistedAddresses == 0) return WaitingMailNone;
        var ordered = shown.OrderByDescending(item => item.Row.OldestBusyObservedAgeMilliseconds ?? -1)
            .ThenByDescending(item => item.Row.PendingCount).ThenBy(item => item.Session.Label, StringComparer.Ordinal)
            .ThenBy(item => item.Session.SessionId, StringComparer.Ordinal).ToArray();
        var parts = ordered.Take(WaitingMailShownSessions).Select(item => WaitingMailLine(item.Session, item.Row, view.NoticeAfterSeconds)).ToList();
        int more = ordered.Length - WaitingMailShownSessions;
        if (more > 0) parts.Add("and " + Invariant(more) + (more == 1 ? " more session" : " more sessions"));
        if (unlistedAddresses > 0)
            parts.Add(Invariant(unlistedPending) + " pending for " + Invariant(unlistedAddresses) +
                (unlistedAddresses == 1 ? " address" : " addresses") + " not in the current session list");
        return string.Join("; ", parts) + ". " + WaitingMailLowerBound;
    }

    private static string WaitingMailLine(SessionView session, WaitingMailSessionView row, int noticeAfterSeconds)
    {
        string label = Safe(session.Label, 48).Replace('"', '\'');
        var line = new StringBuilder("\"" + (string.IsNullOrWhiteSpace(label) ? "Unnamed" : label) + "\" (" + Safe(session.Adapter, 24) + "): " +
            Invariant(row.PendingCount) + " pending");
        // QueuedCount is Accepted mail only. PendingCount also holds Submitting, Submitted and Uncertain mail, and none of them
        // is Delivered, so the queued count is never called "not yet delivered" (that would read the rest as Delivered).
        if (row.QueuedCount > 0) line.Append(", " + Invariant(row.QueuedCount) + " still queued");
        // For a Codex recipient BusyCount can be lower than QueuedCount, so neither count is derived from the other.
        if (row.BusyCount > 0)
        {
            line.Append(", " + Invariant(row.BusyCount) + " waiting while it is busy");
            // The worker's monotonic age, never OldestBusyObservedAtUtc against this machine's clock.
            if (row.OldestBusyObservedAgeMilliseconds is long age && age >= 0)
            {
                line.Append(age < 60_000 ? ", observed busy for under a minute" : ", observed busy for at least " + WaitingMailAge(age));
                // The threshold is the worker's own NoticeAfterSeconds, never a second literal here.
                if (age >= (long)noticeAfterSeconds * 1000) line.Append(", past the " + NoticeAfterText(noticeAfterSeconds) + " sender notice threshold");
            }
        }
        return line.ToString();
    }

    private static bool WaitingMailUsable(WaitingMailView view) => view.NoticeAfterSeconds > 0 && view.Sessions is not null &&
        view.Sessions.All(row => row is not null && row.SessionId is not null && row.PendingCount >= 0 && row.QueuedCount >= 0 &&
            row.BlockedCount >= 0 && row.BusyCount >= 0 && row.UnobservedBlockedCount >= 0);

    // BSM-4 passive signal (PLAN 3.6 under Addendum A.3): how many current sessions have mail that has waited, while they were
    // busy, for at least the worker's own notice threshold. Status and the tray root share this one pure function of the
    // same snapshot and answer the Waiting mail row draws, so both clear with the row: restart, shutdown, disabling and lost
    // status leave no snapshot or no answer, and an unusable answer is never eligible. No question, sound, speech, timer or
    // episode state. The status row's Activity is not consulted: a Codex row can read Unknown while the worker's row proves a
    // fresh adapter Busy. The age is the oldest qualifying message's only, so this counts sessions, never messages.
    internal static int WaitingMailSignalSessions(bool enabled, bool shutDown, BrokerSnapshot? status, ControlResponse? response)
    {
        if (!enabled || shutDown || status is null) return 0;
        if (response is not { Success: true, Code: "ok", WaitingMail: { } mail } || !WaitingMailUsable(mail)) return 0;
        if (mail.BrokerId != status.BrokerId) return 0;
        // The worker's NoticeAfterSeconds, never a second literal; its monotonic age, never this machine's clock.
        long noticeAfter = (long)mail.NoticeAfterSeconds * 1000;
        return status.Sessions.Count(session => session is not null && mail.Sessions.Any(row =>
            row.SessionId == session.SessionId &&
            row.Incarnation == session.Incarnation &&
            row.PendingCount > 0 &&
            row.BusyCount > 0 &&
            row.OldestBusyObservedAgeMilliseconds is long age && age >= 0 && age >= noticeAfter));
    }

    private int WaitingMailSignalSessions() => WaitingMailSignalSessions(settings.Enabled, shutDown, snapshot, waitingMail);

    // Appended to Status after the core worker status and any incoming pairing text, never in place of them. It says only that
    // mail has waited past the threshold; it never says a notice was sent, that work is stuck or that anything was delivered.
    internal static string WaitingMailStatusSignal(int sessions) => sessions <= 0 ? "" :
        " Mail has waited past the notice threshold for " + (sessions == 1 ? "a busy session" : Invariant(sessions) + " busy sessions") +
        "; see Waiting mail.";

    // The tray root label read when the menu opens: the waiting-mail part follows the pairing signal when one is shown.
    internal static string WaitingMailTrayLabel(string root, int sessions) => sessions <= 0 ? root :
        root + (root == "AgentBroker" ? ": " : "; ") + "mail waiting for " + (sessions == 1 ? "a busy session" : Invariant(sessions) + " busy sessions");

    private static string WaitingMailAge(long milliseconds)
    {
        long minutes = milliseconds / 60_000;
        return minutes < 60 ? Invariant(minutes) + " min" : Invariant(minutes / 60) + " h " + Invariant(minutes % 60) + " min";
    }

    private static string NoticeAfterText(int seconds) => seconds % 60 == 0 ? Invariant(seconds / 60) + " min" : Invariant(seconds) + " s";

    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);
}
