using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using AgentBroker.CodexDesktopAdapter;
using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Adapters;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

// N-broker-native-confirmation-01, stage evidence (AgentBroker 1.10.6, lane N round 2, DECISIONS 127): the report-only
// native_submission_stage line BrokerRuntime.DeliverAsync writes for each settled attempt of a local native adapter. Labels 1 to 7
// drive the real DeliverAsync through a started BrokerRuntime; labels 8 and 9 are pure DeliveryObservations checks of the bounded
// lifetimeOrdinal. This file stops at its first failure: one check per label, in this order, so each killer reports at its own
// label (the killer list and its first-failure arguments live outside the repo).
//
// Fixture. One store and one runtime on a StageClock: wall time fixed (leases, mailboxes and messages never lapse or expire, and
// eventAt is known), monotonic time moved only by the scripted Codex provider, 1234 ms per submit (so elapsedMs is exact, and the
// store's 120 s uncertainty-advisory grace never comes due: about ten submits in all). The runtime holds a real CodexNativeAdapter
// over that provider (one conversation with Receive, WakeIdle and the idle guard, activity Unknown, so it is claimed) and a local
// non-native adapter (synthetic-stage, Idle, answers Delivered); a fake paired broker answers every forward Delivered. The sender
// is a register_agent mailbox (Receive|Reply, never Ready), so the notices it is owed are never claimed and no non-native
// recipient reaches DeliverAsync before label 6. Every events read goes through EventsFile (FileShare.ReadWrite|Delete, complete
// lines only), and every line read selects native_submission_stage lines only, since the discovery loops write discovery_pass
// lines into the same file; label 5's raw scan alone reads the whole file.
// Setup waits throw NATIVE_STAGE_EVENT_SETUP, never a graded label; graded waits return false and never throw, and every graded
// wait on a line also waits for that message's final receipt (except label 2's wait for B's first deferral, which reads only that
// line's content), so a line written before its settlement grades the same.
//
// Timing (N8, N26). Each of B's three pre-frame deferrals parks it for 5 s on DateTimeOffset.UtcNow (DeferredDeliveryClaims'
// ReleaseUnsubmittedClaim sets the time and BeginDelivery lifts it), which no TimeProvider controls, so B's four publications take
// at least 15 s of wall-clock time plus loop delays. Label 4 waits up to 45 s, and B's lifetime is 10 minutes (the frozen store
// clock never reaches it anyway).
//
// Ordering (X-F2, N7). C goes to a second register_agent mailbox (Receive|Reply, no WakeIdle): BrokerStore.Ready never holds for it,
// so it is never claimed. It is a no-dispatch negative (its receipt is still the Accepted one Send returned, zero attempts, no line)
// and proves nothing about Observe. D (the local non-native adapter) and E (a paired broker's codex: address) are real completed
// attempts: the check waits for each one's own witness (the adapter or the peer saw it) and its final Delivered receipt before
// sentinel F is sent. A settlement commit and its Observe run inside one hold of the claims gate, and F can only be claimed by a
// BeginDelivery that takes that gate after F existed, so after D's and E's Observe returned; F's line follows F's claim. So when
// F's line is read, any line D or E would write is already in the file. G (label 7): the provider blocks inside G's submit until
// the check has recorded the recipient's own Delivered receipt for G, then sets its own returned signal and answers Uncertain; G's
// settlement is then rejected (the claim no longer matches) and Observe never runs. H is sent only after that returned signal and
// G's Delivered receipt are observed. A write misplaced before settlement runs on G's delivery task right after the provider
// returns, with only the adapter's return path and Normalize between, all in memory; H's line needs the check to see the signal,
// a state commit for H, a delivery loop's next pass (50 to 500 ms apart), a claim commit, H's own submit and a settlement commit
// first. So such a line precedes H's line, and the read after H's line sees it.
internal static class NativeStageEventChecks
{
    private const string Written = "NATIVE_STAGE_EVENT_WRITTEN: one settled Codex attempt leaves exactly one native_submission_stage line naming its message, exact recipient, outcome, reason, stage answer_wait, monotonic duration and store time";
    private const string NamesStage = "NATIVE_STAGE_EVENT_NAMES_STAGE: a typed Codex pre-frame deferral reads pre_frame, an ambiguous raised code after the provider's submit marker reads other, and every reason of the static table reads its own family";
    private const string ReceiptAttempts = "NATIVE_STAGE_EVENT_RECEIPT_ATTEMPTS: each line carries the claim's public receipt Attempts, 1 for every attempt";
    private const string LifetimeOrdinal = "NATIVE_STAGE_EVENT_LIFETIME_ORDINAL: three pre-frame deferrals and the fourth Uncertain of one message carry lifetimeOrdinal 1, 2, 3 and 4, each with receiptAttempts 1";
    private const string NoText = "NATIVE_STAGE_EVENT_NO_TEXT: no events line holds the message text, the sender or any label, and every stage line has its ten keys in order and nothing after them but eventsLost";
    private const string NativeOnly = "NATIVE_STAGE_EVENT_NATIVE_ONLY: a normal mailbox is never dispatched, and completed attempts through a local non-native adapter and to a paired broker's codex: address write no line";
    private const string RejectedNoLine = "NATIVE_STAGE_EVENT_REJECTED_NO_LINE: an attempt whose settlement is rejected, the recipient having acknowledged the message meanwhile, writes no line";
    private const string OrdinalResets = "NATIVE_STAGE_ORDINAL_RESETS: the ordinal restarts at 1 for a new admission, a new lifetime and another recipient incarnation of the same message ID";
    private const string OrdinalBounded = "NATIVE_STAGE_ORDINAL_BOUNDED: at capacity the least recently counted entry is evicted, so the map stays at the bound, an evicted lifetime revisited restarts at 1 and a retained one continues";

    private const string StageEvent = "native_submission_stage";
    // Synthetic texts that must never reach the events file. The token has letters no GUID or timestamp holds.
    private const string TextToken = "stagetextzq7w";
    private const string MessageText = "Synthetic stage evidence " + TextToken + ": never in an events line.";
    private const string SenderLabel = "Synthetic stage sender";
    private const string MailboxLabel = "Synthetic stage mailbox";
    private const string LocalLabel = "Synthetic stage local";
    private const string PeerName = "Synthetic stage peer";
    private const string RaisedReason = "native_uncertain_raised_connection_closed";
    private static readonly string[] StageKeys =
        ["event", "messageId", "recipient", "receiptAttempts", "lifetimeOrdinal", "outcome", "reason", "stage", "elapsedMs", "eventAt"];
    private static readonly DesktopSubmissionResult PreFrameTimeout =
        new(DesktopSubmissionOutcome.DeferredNotSubmitted, ErrorCode: "rediscovery_timeout", ConnectionClosed: true);

    private const string Codex = "codex-native", Claude = "claude-native";
    private const string PreFrame = "pre-frame", PostFrame = "post-frame", Ambiguous = "ambiguous";

    // Label 2's static table: every mapped reason with its expected family and its frame, pre-frame (the adapter proved no model
    // input was written), post-frame (the turn frame was sent or answered) or ambiguous (the reason does not say which side). Each
    // expectation is written out per reason and never derived from a prefix: every Codex native_uncertain_raised_<code> other than the
    // owner-change codes is listed as other, raised_io and raised_cancelled included (the fourth typed pre-frame result reuses them).
    // response_timeout_or_cancelled is a library code the Codex adapter maps before the runtime (compatibility only), and
    // native_submission_uncertain has two Codex producers (ambiguous, filed under answer_wait as a judgment).
    private static readonly (string Adapter, AdapterAdmission Outcome, string? Reason, string Family, string Frame)[] StageTable =
    [
        (Codex, AdapterAdmission.Submitted, "native_admission_unconfirmed", "submitted", PostFrame),
        (Codex, AdapterAdmission.Delivered, null, "delivered", PostFrame),
        (Codex, AdapterAdmission.DeferredNotSubmitted, "native_owner_changed", "owner_changed", PreFrame),
        (Codex, AdapterAdmission.Uncertain, "native_uncertain_owner_changed", "owner_changed", PostFrame),
        (Codex, AdapterAdmission.Uncertain, "native_uncertain_raised_owner_changed", "owner_changed", Ambiguous),
        (Codex, AdapterAdmission.Uncertain, "native_uncertain_raised_server_incarnation_changed", "owner_changed", Ambiguous),
        (Codex, AdapterAdmission.Uncertain, "native_uncertain_raised_stale_binding", "owner_changed", Ambiguous),
        (Codex, AdapterAdmission.DeferredNotSubmitted, "native_owner_rediscovery_unavailable", "pre_frame", PreFrame),
        (Codex, AdapterAdmission.Uncertain, "native_uncertain_rediscovery_timeout", "pre_frame", PreFrame),
        (Codex, AdapterAdmission.DeferredNotSubmitted, "native_owner_unavailable", "pre_frame", PreFrame),
        (Codex, AdapterAdmission.Uncertain, "native_uncertain_timeout", "answer_wait", PostFrame),
        (Codex, AdapterAdmission.Uncertain, "response_timeout_or_cancelled", "answer_wait", PostFrame),
        (Codex, AdapterAdmission.Uncertain, "native_uncertain_native_error", "answer_wait", PostFrame),
        (Codex, AdapterAdmission.Uncertain, "native_submission_uncertain", "answer_wait", Ambiguous),
        (Codex, AdapterAdmission.Uncertain, "native_uncertain_connection", "connection", PostFrame),
        (Codex, AdapterAdmission.DeferredNotSubmitted, "native_receipt_capacity_reconnect", "connection", PreFrame),
        (Codex, AdapterAdmission.Uncertain, "native_uncertain_raised_io", "other", Ambiguous),
        (Codex, AdapterAdmission.Uncertain, "native_uncertain_raised_cancelled", "other", Ambiguous),
        (Codex, AdapterAdmission.Uncertain, "native_uncertain_raised_connection_closed", "other", Ambiguous),
        (Codex, AdapterAdmission.Uncertain, "native_uncertain_raised_message_id_collision", "other", Ambiguous),
        (Codex, AdapterAdmission.Uncertain, "native_uncertain_raised_owner_discovery_rejected", "other", Ambiguous),
        (Codex, AdapterAdmission.Uncertain, "native_uncertain_raised_protocol_mismatch", "other", Ambiguous),
        (Codex, AdapterAdmission.DeferredNotSubmitted, "native_context_busy", "other", PreFrame),
        (Codex, AdapterAdmission.DeferredNotSubmitted, "native_not_submitted", "other", PreFrame),
        (Codex, AdapterAdmission.DeferredNotSubmitted, "native_verification_unavailable", "other", PreFrame),
        (Codex, AdapterAdmission.DeferredNotSubmitted, "native_session_unavailable", "other", PreFrame),
        (Codex, AdapterAdmission.DeferredNotSubmitted, "native_delivery_wait_cancelled", "other", PreFrame),
        (Codex, AdapterAdmission.Refused, "native_delivery_disabled", "other", PreFrame),
        (Codex, AdapterAdmission.Refused, "native_message_too_large", "other", PreFrame),
        (Codex, AdapterAdmission.Refused, "codex_observation_only", "other", PreFrame),
        (Codex, AdapterAdmission.Refused, "native_client_profile_unverified", "other", PreFrame),
        (Codex, AdapterAdmission.Refused, "unsupported_capability", "other", PreFrame),
        (Codex, AdapterAdmission.Refused, "native_admission_refused", "other", PostFrame),
        (Codex, AdapterAdmission.Uncertain, "submission_unconfirmed", "other", Ambiguous),
        (Codex, AdapterAdmission.Uncertain, "submission_failed", "other", Ambiguous),
        (Codex, AdapterAdmission.Uncertain, "adapter_result_invalid", "other", Ambiguous),
        (Codex, AdapterAdmission.Uncertain, "adapter_reason_invalid", "other", Ambiguous),
        (Codex, AdapterAdmission.Uncertain, null, "other", Ambiguous),
        (Claude, AdapterAdmission.Submitted, "native_write_unconfirmed", "submitted", PostFrame),
        (Claude, AdapterAdmission.DeferredNotSubmitted, "native_owner_changed", "owner_changed", PreFrame),
        (Claude, AdapterAdmission.DeferredNotSubmitted, "native_transport_unavailable", "connection", PreFrame),
        (Claude, AdapterAdmission.Uncertain, "native_uncertain_raised_io", "connection", Ambiguous),
        (Claude, AdapterAdmission.Uncertain, "native_uncertain_raised_access", "connection", Ambiguous),
        (Claude, AdapterAdmission.Uncertain, "native_uncertain_raised_cancelled", "other", Ambiguous),
        (Claude, AdapterAdmission.Uncertain, "native_uncertain_raised_timeout", "other", Ambiguous),
        (Claude, AdapterAdmission.Uncertain, "native_submission_uncertain", "other", Ambiguous),
        (Claude, AdapterAdmission.Uncertain, "native_admission_unconfirmed", "other", Ambiguous),
        (Claude, AdapterAdmission.DeferredNotSubmitted, "native_recipient_busy", "other", PreFrame),
        (Claude, AdapterAdmission.DeferredNotSubmitted, "native_recipient_awaiting_approval", "other", PreFrame),
        (Claude, AdapterAdmission.DeferredNotSubmitted, "native_metadata_unavailable", "other", PreFrame),
        (Claude, AdapterAdmission.DeferredNotSubmitted, "native_not_submitted", "other", PreFrame),
        (Claude, AdapterAdmission.Refused, "claude_credentials_disabled", "other", PreFrame),
        (Claude, AdapterAdmission.Refused, "claude_observation_only", "other", PreFrame),
    ];

    internal static async Task RunAsync(Action<bool, string> check)
    {
        await RuntimeAsync(check);
        ResetsCheck(check);
        BoundedCheck(check);
    }

    // Labels 1 to 7, in one runtime.
    private static async Task RuntimeAsync(Action<bool, string> check)
    {
        using var scratchOwner = OwnedScratch.Create("native-stage-events");
        string directory = Path.Combine(scratchOwner.Root, "store");
        var clock = new StageClock();
        using var store = new BrokerStore(directory, clock: clock);
        var peerId = Guid.NewGuid();
        store.AuthorizePeer(new(peerId, PeerName, "https://192.0.2.12/", new string('D', 64), clock.GetUtcNow()));
        var provider = new StageProvider(clock);
        var local = new StageLocalAdapter(new SessionAddress(store.BrokerId, "synthetic:stage-" + Guid.NewGuid().ToString("N"), Guid.NewGuid()), clock);
        var peer = new StagePeer();
        var runtime = new BrokerRuntime(store, peer, [new CodexNativeAdapter(provider, enableDelivery: true, clock: clock), local], clock: clock)
            { DiscoveryInterval = TimeSpan.FromMilliseconds(100) };
        runtime.Start(default);
        try
        {
            await SetupAsync(() => store.Directory().Any(row => row.Adapter == Codex) && store.Directory().Any(row => row.Address == local.Address),
                "the Codex row and the local row were not discovered");
            var codexRow = store.Directory().Single(row => row.Adapter == Codex);
            string codexAddress = Address(codexRow.Address);
            var sender = runtime.RegisterPollingAgent(Guid.NewGuid(), Guid.NewGuid(), SenderLabel, false);
            var mailbox = runtime.RegisterPollingAgent(Guid.NewGuid(), Guid.NewGuid(), MailboxLabel, false);
            MessageReceipt Send(SessionAddress recipient, StageScript? script = null)
            {
                var id = Guid.NewGuid();
                if (script is not null) provider.Script(id, script);
                return store.Send(sender.Address, recipient, id, MessageText, null, 10, store.BrokerId);
            }
            bool Final(Guid id, MessageStatus status, string? reason) =>
                store.FindReceipt(id) is { } current && current.Status == status && current.Reason == reason;

            // 1: message A, answered Uncertain response_timeout_or_cancelled (the adapter's native_uncertain_timeout). Values only: not
            // receiptAttempts, not lifetimeOrdinal, not the key set.
            var a = Send(codexRow.Address).Message.Id;
            bool aSettled = await UntilAsync(() => Stage(directory).Count >= 1 && Final(a, MessageStatus.Uncertain, "native_uncertain_timeout"), 15);
            check(aSettled && Holds(() => Stage(directory) is [var line] && Text(line, "event") == StageEvent &&
                Text(line, "messageId") == a.ToString("D") && Text(line, "recipient") == codexAddress &&
                Text(line, "outcome") == "Uncertain" && Text(line, "reason") == "native_uncertain_timeout" &&
                Text(line, "stage") == "answer_wait" && Long(line, "elapsedMs") == 1234 && EventAt(line) == clock.GetUtcNow()), Written);

            // 2: R raises a coded fault after the provider's submit marker (an ambiguous raised reason), then B's first attempt is a
            // typed pre-frame deferral; and the static table.
            var r = Send(codexRow.Address, StageScript.Raise("connection_closed")).Message.Id;
            bool rSettled = await UntilAsync(() => Lines(directory, r).Length == 1 && Final(r, MessageStatus.Uncertain, RaisedReason), 15);
            var b = Send(codexRow.Address, StageScript.Answer(PreFrameTimeout)).Message.Id;
            bool bFirst = await UntilAsync(() => Lines(directory, b).Length >= 1, 15);
            check(rSettled && bFirst && provider.Marked(r) && Holds(() => Lines(directory, b) is [var first, ..] &&
                Text(first, "outcome") == "DeferredNotSubmitted" && Text(first, "reason") == "native_owner_rediscovery_unavailable" &&
                Text(first, "stage") == "pre_frame" && Lines(directory, r) is [var raised] && Text(raised, "outcome") == "Uncertain" &&
                Text(raised, "reason") == RaisedReason && Text(raised, "stage") == "other") && TableHolds(), NamesStage);

            // 3: the public receipt Attempts on A's, R's and B's first line.
            check(Holds(() => Lines(directory, a) is [var aLine] && Long(aLine, "receiptAttempts") == 1 &&
                Lines(directory, r) is [var rLine] && Long(rLine, "receiptAttempts") == 1 &&
                Lines(directory, b) is [var bLine, ..] && Long(bLine, "receiptAttempts") == 1), ReceiptAttempts);

            // 4: B's four publications in file order (at least 15 s of wall-clock backoff), and the single attempts of A and R.
            bool bDone = await UntilAsync(() => Lines(directory, b).Length >= 4 &&
                Final(b, MessageStatus.Uncertain, "native_uncertain_rediscovery_timeout"), 45);
            check(bDone && Holds(() => Lines(directory, b) is { Length: 4 } bLines &&
                bLines.Select(line => Long(line, "lifetimeOrdinal")).SequenceEqual(new long?[] { 1, 2, 3, 4 }) &&
                bLines.All(line => Long(line, "receiptAttempts") == 1 && Text(line, "stage") == "pre_frame" && Text(line, "recipient") == codexAddress) &&
                bLines.Select(line => Text(line, "outcome")).SequenceEqual(new string?[] { "DeferredNotSubmitted", "DeferredNotSubmitted", "DeferredNotSubmitted", "Uncertain" }) &&
                bLines.Select(line => Text(line, "reason")).SequenceEqual(new string?[] { "native_owner_rediscovery_unavailable",
                    "native_owner_rediscovery_unavailable", "native_owner_rediscovery_unavailable", "native_uncertain_rediscovery_timeout" }) &&
                Lines(directory, a) is [var aOrdinal] && Long(aOrdinal, "lifetimeOrdinal") == 1 &&
                Lines(directory, r) is [var rOrdinal] && Long(rOrdinal, "lifetimeOrdinal") == 1), LifetimeOrdinal);

            // 5: the whole events file (discovery_pass lines included) holds no text, sender or label; every stage line has its ten keys in order, then at most eventsLost.
            string[] forbidden = [TextToken, MessageText, sender.Address.SessionId, SenderLabel, MailboxLabel, LocalLabel, codexRow.Label, PeerName];
            check(Holds(() => Encoding.UTF8.GetString(EventsFile.Bytes(directory)) is var raw &&
                forbidden.All(text => !raw.Contains(text, StringComparison.Ordinal)) &&
                Stage(directory) is { Count: > 0 } keyed && keyed.All(TenKeys)), NoText);

            // 6: C, the no-dispatch negative; D and E, completed non-native attempts with their own witnesses and final receipts;
            // then sentinel F (see the ordering argument above).
            var c = Send(mailbox.Address);
            var d = Send(local.Address).Message.Id;
            var peerAddress = new SessionAddress(peerId, "codex:" + Guid.NewGuid().ToString("D"), Guid.NewGuid());
            var e = Send(peerAddress).Message.Id;
            bool completed = await UntilAsync(() => local.Saw(d) && Final(d, MessageStatus.Delivered, null) &&
                peer.Saw(e) && Final(e, MessageStatus.Delivered, "peer_receipt"), 15);
            var f = Send(codexRow.Address).Message.Id;
            bool sentinel = completed && await UntilAsync(() => Lines(directory, f).Length == 1 &&
                Final(f, MessageStatus.Uncertain, "native_uncertain_timeout"), 15);
            string?[] otherIds = [c.Message.Id.ToString("D"), d.ToString("D"), e.ToString("D")];
            string?[] otherRecipients = [Address(mailbox.Address), Address(local.Address), Address(peerAddress)];
            check(sentinel && Holds(() => store.FindReceipt(c.Message.Id) is { Status: MessageStatus.Accepted, Attempts: 0 } unclaimed &&
                unclaimed.Equals(c) && Stage(directory) is { Count: 7 } scoped &&
                scoped.All(line => !otherIds.Contains(Text(line, "messageId")) && !otherRecipients.Contains(Text(line, "recipient")))), NativeOnly);

            // 7: G's provider holds until the recipient's own Delivered receipt is recorded, then answers; sentinel H after the
            // provider's returned signal and G's Delivered receipt (see the ordering argument above).
            var g = Send(codexRow.Address, StageScript.Hold()).Message.Id;
            bool entered = await UntilAsync(() => provider.Entered(g), 15);
            bool acknowledged = false;
            if (entered)
            {
                try { acknowledged = store.RecordRecipientReceipt(g, codexRow.Address, store.BrokerId, MessageStatus.Delivered).Status == MessageStatus.Delivered; }
                catch (Exception) { acknowledged = false; }
            }
            provider.Release();
            bool returned = await UntilAsync(() => provider.Returned(g) && Final(g, MessageStatus.Delivered, "recipient_receipt"), 15);
            var h = Send(codexRow.Address).Message.Id;
            bool barrier = returned && await UntilAsync(() => Lines(directory, h).Length == 1 &&
                Final(h, MessageStatus.Uncertain, "native_uncertain_timeout"), 15);
            check(entered && acknowledged && barrier && Holds(() => Lines(directory, g).Length == 0 && Stage(directory).Count == 8), RejectedNoLine);
        }
        finally
        {
            provider.Release();
            // Disposed before the scratch goes; a loop fault rethrown here must not hide the label that already failed.
            try { await runtime.DisposeAsync(); }
            catch (Exception) { }
        }
    }

    // 8: a pure check of the ordinal's restarts.
    private static void ResetsCheck(Action<bool, string> check)
    {
        var observations = new DeliveryObservations(TimeProvider.System);
        var recipient = new SessionAddress(Guid.NewGuid(), "codex:" + Guid.NewGuid().ToString("D"), Guid.NewGuid());
        var message = PureMessage(recipient);
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        int[] firstAdmission = [observations.CountAttempt(message, first), observations.CountAttempt(message, first), observations.CountAttempt(message, first)];
        int[] secondAdmission = [observations.CountAttempt(message, second), observations.CountAttempt(message, second)];
        // The same UUID in a new lifetime (another CreatedAt and ExpiresAt), then to another recipient incarnation.
        var nextLifetime = message with { CreatedAt = message.CreatedAt.AddSeconds(1), ExpiresAt = message.ExpiresAt.AddSeconds(1) };
        int[] lifetimeCounts = [observations.CountAttempt(nextLifetime, second), observations.CountAttempt(nextLifetime, second)];
        var otherIncarnation = nextLifetime with { Recipient = recipient with { Incarnation = Guid.NewGuid() } };
        int incarnationCount = observations.CountAttempt(otherIncarnation, second);
        int otherMessage = observations.CountAttempt(PureMessage(recipient), first);
        check(firstAdmission.SequenceEqual(new[] { 1, 2, 3 }) && secondAdmission.SequenceEqual(new[] { 1, 2 }) &&
            lifetimeCounts.SequenceEqual(new[] { 1, 2 }) && incarnationCount == 1 && otherMessage == 1, OrdinalResets);
    }

    // 9: a pure check of the bound, with the eviction reset: an evicted lifetime revisited (same epoch and admission) restarts at 1,
    // while a retained one continues.
    private static void BoundedCheck(Action<bool, string> check)
    {
        var observations = new DeliveryObservations(TimeProvider.System);
        var recipient = new SessionAddress(Guid.NewGuid(), "codex:" + Guid.NewGuid().ToString("D"), Guid.NewGuid());
        var admission = Guid.NewGuid();
        var retained = PureMessage(recipient);
        int retainedFirst = observations.CountAttempt(retained, admission);
        var fillers = Enumerable.Range(0, DeliveryObservations.MaximumMessages - 1).Select(_ => PureMessage(recipient)).ToArray();
        bool fillersFirst = true;
        foreach (var filler in fillers) fillersFirst &= observations.CountAttempt(filler, admission) == 1;
        bool full = observations.AttemptEntries == DeliveryObservations.MaximumMessages;
        // Counted again, the retained message becomes the most recent; one more message evicts the least recently counted filler.
        int retainedSecond = observations.CountAttempt(retained, admission);
        int newcomer = observations.CountAttempt(PureMessage(recipient), admission);
        bool bounded = observations.AttemptEntries == DeliveryObservations.MaximumMessages;
        int evictedRevisit = observations.CountAttempt(fillers[0], admission);
        int retainedThird = observations.CountAttempt(retained, admission);
        bool stillBounded = observations.AttemptEntries == DeliveryObservations.MaximumMessages;
        check(retainedFirst == 1 && fillersFirst && full && retainedSecond == 2 && newcomer == 1 && bounded &&
            evictedRevisit == 1 && retainedThird == 3 && stillBounded, OrdinalBounded);
    }

    private static BrokerMessage PureMessage(SessionAddress recipient) => new(Guid.NewGuid(), Guid.NewGuid(), null,
        new(recipient.BrokerId, "mcp:" + Guid.NewGuid().ToString("D"), Guid.NewGuid()), recipient, MessageText,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(10));

    private static bool TableHolds() =>
        StageTable.All(row => BrokerRuntime.NativeStageFamily(row.Adapter, row.Outcome, row.Reason) == row.Family) &&
        // The table's own rule: pre_frame is claimed only for a reason whose frame is pre-frame.
        StageTable.All(row => row.Frame is PreFrame or PostFrame or Ambiguous && (row.Family != "pre_frame" || row.Frame == PreFrame));

    private static bool TenKeys(JsonObject line)
    {
        var keys = line.Select(pair => pair.Key).ToArray();
        return keys.SequenceEqual(StageKeys) || keys.SequenceEqual(StageKeys.Append("eventsLost"));
    }

    private static async Task SetupAsync(Func<bool> condition, string what)
    {
        if (!await UntilAsync(condition, 15)) throw new InvalidOperationException("NATIVE_STAGE_EVENT_SETUP: " + what);
    }

    // An evaluation that meets an unexpected shape, or an events file it cannot read, is a failed check, never an unlabelled exception.
    private static bool Holds(Func<bool> evaluate)
    {
        try { return evaluate(); }
        catch (Exception) { return false; }
    }

    // Polls every 20 ms; false at the limit, never a throw.
    private static async Task<bool> UntilAsync(Func<bool> condition, double seconds)
    {
        var watch = Stopwatch.StartNew();
        while (!Holds(condition))
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(seconds)) return false;
            await Task.Delay(20);
        }
        return true;
    }

    private static List<JsonObject> Stage(string directory) =>
        EventsFile.Read(directory).Where(line => Text(line, "event") == StageEvent).ToList();

    private static JsonObject[] Lines(string directory, Guid messageId) =>
        Stage(directory).Where(line => Text(line, "messageId") == messageId.ToString("D")).ToArray();

    private static string Address(SessionAddress address) =>
        address.BrokerId.ToString("D") + "/" + address.SessionId + "/" + address.Incarnation.ToString("D");

    private static string? Text(JsonObject line, string key) => line[key] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static long? Long(JsonObject line, string key) => line[key] is JsonValue value && value.TryGetValue(out long number) ? number : null;

    // The parsed value, never the raw text: the encoder writes the plus sign as a six-character JSON Unicode escape.
    private static DateTimeOffset? EventAt(JsonObject line) => Text(line, "eventAt") is { } text &&
        DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null;

    // Wall time fixed, monotonic time moved only by Advance (a copy of CodexPreFrameChecks' StepClock, with interlocked access,
    // since the provider advances it on a delivery loop while discovery and the check read it).
    private sealed class StageClock : TimeProvider
    {
        private readonly DateTimeOffset utc = DateTimeOffset.UtcNow;
        private long timestamp = 1;
        public override DateTimeOffset GetUtcNow() => utc;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref timestamp);
        internal void Advance(TimeSpan duration) => Interlocked.Add(ref timestamp, duration.Ticks);
    }

    // What the provider does with one message: answer with a result, raise a coded library fault after its submit marker, or hold
    // until the check releases it and then answer Uncertain.
    private sealed class StageScript
    {
        private StageScript(DesktopSubmissionResult? result, string? raise, bool hold) { Result = result; RaiseCode = raise; HoldUntilReleased = hold; }
        internal DesktopSubmissionResult? Result { get; }
        internal string? RaiseCode { get; }
        internal bool HoldUntilReleased { get; }
        internal static StageScript Answer(DesktopSubmissionResult result) => new(result, null, false);
        internal static StageScript Raise(string code) => new(null, code, false);
        internal static StageScript Hold() => new(null, null, true);
    }

    // One Codex conversation whose owner always resolves to the same exact-pinned binding (CodexPreFrameChecks' ScriptedProvider
    // shape). Every submit moves the monotonic clock 1234 ms and reaches its submit marker (past it a real provider may have written
    // the turn) before it follows the message's script; an unscripted message is answered Uncertain response_timeout_or_cancelled.
    private sealed class StageProvider(StageClock clock) : ICodexNativeProvider
    {
        private static readonly DesktopSubmissionResult TimedOut = new(DesktopSubmissionOutcome.Uncertain, ErrorCode: "response_timeout_or_cancelled");
        private readonly Guid thread = Guid.NewGuid(), ownerClient = Guid.NewGuid(), ownerIncarnation = Guid.NewGuid();
        private readonly ConcurrentDictionary<Guid, StageScript> scripts = new();
        private readonly ConcurrentDictionary<Guid, byte> marked = new(), entered = new(), returned = new();
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private DesktopOwnerBinding Binding => new(thread, ownerClient, ownerIncarnation, 12345, 134000000000000000, true)
            { RouterTrust = DesktopRouterTrust.ExactPinned };
        public bool NativeIdleGuard => true;
        internal void Script(Guid id, StageScript script) => scripts[id] = script;
        internal bool Marked(Guid id) => marked.ContainsKey(id);
        internal bool Entered(Guid id) => entered.ContainsKey(id);
        internal bool Returned(Guid id) => returned.ContainsKey(id);
        internal void Release() => release.TrySetResult();

        public Task<IReadOnlyList<DesktopDirectorySession>> ObserveAsync(CancellationToken token)
            => Task.FromResult<IReadOnlyList<DesktopDirectorySession>>([new(thread, DesktopSessionActivity.Unknown, clock.GetUtcNow(), null)]);

        public Task<DesktopOwnerBinding?> ResolveOwnerAsync(Guid conversationId, CancellationToken token)
            => Task.FromResult<DesktopOwnerBinding?>(conversationId == thread ? Binding : null);

        public async Task<DesktopSubmissionResult> SubmitAsync(DesktopOwnerBinding current, Guid id, string text, CancellationToken token)
        {
            clock.Advance(TimeSpan.FromMilliseconds(1234));
            scripts.TryGetValue(id, out var script);
            marked[id] = 0;
            if (script?.RaiseCode is { } code) throw new DesktopAdapterException(code);
            if (script?.HoldUntilReleased == true)
            {
                entered[id] = 0;
                await Task.WhenAny(release.Task, Task.Delay(TimeSpan.FromSeconds(30)));
                returned[id] = 0;
                return TimedOut;
            }
            return script?.Result ?? TimedOut;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // A local adapter that is neither codex-native nor claude-native: one Idle row that can be woken, and every submit Delivered.
    private sealed class StageLocalAdapter(SessionAddress address, TimeProvider clock) : ILocalAgentAdapter
    {
        private readonly ConcurrentDictionary<Guid, byte> seen = new();
        internal SessionAddress Address => address;
        public string Id => "synthetic-stage";
        internal bool Saw(Guid id) => seen.ContainsKey(id);

        public Task<IReadOnlyList<BrokerSession>> DiscoverAsync(Guid brokerId, CancellationToken token)
            => Task.FromResult<IReadOnlyList<BrokerSession>>([new(address, LocalLabel, Id,
                SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply, SessionActivity.Idle, clock.GetUtcNow().AddMinutes(1))]);

        public Task<AdapterResult> SubmitAsync(BrokerMessage message, CancellationToken token)
        {
            seen[message.Id] = 0;
            return Task.FromResult(new AdapterResult(AdapterAdmission.Delivered));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // A paired broker that answers every forward with its recipient's Delivered receipt (PeerForwardChecks' fake shape).
    private sealed class StagePeer : IPeerFederation
    {
        private readonly ConcurrentDictionary<Guid, byte> seen = new();
        internal bool Saw(Guid id) => seen.ContainsKey(id);
        public IReadOnlyList<BrokerAdvertisement> Discovered => [];
        public IReadOnlyList<PairingOffer> PairingOffers => [];
        public BrokerAdvertisement LocalAdvertisement => new(Guid.NewGuid(), "Synthetic", "https://127.0.0.1:15001", new string('C', 64), DateTimeOffset.UtcNow);
        public Guid? AuthenticatePeer(HttpContext context) => null;
        public void MapEndpoints(WebApplication application) { }
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task RequestPairAsync(BrokerAdvertisement peer, CancellationToken token) => throw new NotSupportedException();
        public Task ApprovePairAsync(Guid requestId, CancellationToken token) => throw new NotSupportedException();
        public void RejectPair(Guid requestId) { }
        public void RevokePeer(Guid brokerId) { }
        public Task<PeerDirectory> GetDirectoryAsync(PeerTrust peer, CancellationToken token) => Task.FromResult(new PeerDirectory(peer.BrokerId, []));

        public Task<MessageReceipt> SubmitAsync(PeerTrust peer, BrokerMessage message, CancellationToken token)
        {
            seen[message.Id] = 0;
            return Task.FromResult(new MessageReceipt(message, MessageStatus.Delivered, DateTimeOffset.UtcNow, "recipient_receipt", 1));
        }

        public Task<MessageReceipt?> GetReceiptAsync(PeerTrust peer, Guid messageId, CancellationToken token) => Task.FromResult<MessageReceipt?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
