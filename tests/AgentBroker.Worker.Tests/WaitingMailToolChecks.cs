using AgentBroker.Core;
using AgentBroker.Protocol;
using AgentBroker.Worker;
using Microsoft.AspNetCore.Builder;
using System.Text.Json;

internal static class WaitingMailToolChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("waiting-mail-control");
        var sender = fixture.Register("Synthetic waiting sender");
        var target = fixture.Native("claude:" + Guid.NewGuid()) with { Adapter = "claude-native", Activity = SessionActivity.Busy };
        fixture.Store.Register(target);
        var now = fixture.Clock.GetUtcNow();
        var receipt = fixture.Store.Accept(new(Guid.NewGuid(), Guid.NewGuid(), null, sender.Address, target.Address,
            "Synthetic private waiting text.", now, now.AddMinutes(30)), fixture.Store.BrokerId);
        await using var worker = new ControlWorker(fixture);
        await using var control = new LocalControlServer(worker);
        var legacy = await control.HandleAsync(new("status"), default);
        string json = JsonSerializer.Serialize(legacy, ControlProtocol.Json);
        check(!json.Contains("waitingMail", StringComparison.Ordinal) && !json.Contains("pendingCount", StringComparison.Ordinal), "WAITING_LEGACY_STATUS_SHAPE");
        check(JsonSerializer.Deserialize<LegacyResponse>(json, ControlProtocol.Json)?.Snapshot?.BrokerId == fixture.Store.BrokerId,
            "WAITING_OLD_STRICT_READER");
        var unobserved = await control.HandleAsync(new("waiting-mail"), default);
        check(unobserved.Success && unobserved.Snapshot is null && unobserved.WaitingMail is { } mail &&
            mail.BrokerId == fixture.Store.BrokerId && mail.NoticeAfterSeconds == (int)WaitingMailPolicy.NoticeAfter.TotalSeconds &&
            mail.Sessions.Single(row => row.SessionId == target.Address.SessionId) is { PendingCount: 1, QueuedCount: 1, BusyCount: 0, UnobservedBlockedCount: 1 },
            "WAITING_CONTROL_IDENTITY_POLICY_COUNTS");
        check(fixture.Store.ReadMessageWait(receipt) is null, "WAITING_CONTROL_READ_NOT_OBSERVE");
        var mailbox = fixture.Register("Synthetic counted mailbox");
        fixture.Store.Accept(receipt.Message with { Id = Guid.NewGuid(), Recipient = mailbox.Address }, fixture.Store.BrokerId);
        var generic = (await control.HandleAsync(new("waiting-mail"), default)).WaitingMail!.Sessions.Single(row => row.SessionId == mailbox.Address.SessionId);
        check(generic is { PendingCount: 1, QueuedCount: 1, BusyCount: 0, BlockedCount: 0, OldestBusyObservedAgeMilliseconds: null },
            "WAITING_GENERIC_MAILBOX_COUNT_WITHOUT_BUSY");
        fixture.Store.AdmitDueWaitingMailNotices();
        var observed = await control.HandleAsync(new("waiting-mail"), default);
        check(observed.WaitingMail?.Sessions.Single(row => row.SessionId == target.Address.SessionId) is { BusyCount: 1, OldestBusyObservedAgeMilliseconds: >= 0 } row &&
            row.SessionId == target.Address.SessionId && row.Incarnation == target.Address.Incarnation,
            "WAITING_CONTROL_EXACT_TUPLE_AGE");
        string observedJson = JsonSerializer.Serialize(observed, ControlProtocol.Json);
        using var observedDocument = JsonDocument.Parse(observedJson);
        var rowMembers = new HashSet<string>(StringComparer.Ordinal) { "sessionId", "incarnation", "pendingCount", "queuedCount",
            "blockedCount", "busyCount", "unobservedBlockedCount", "oldestBusyObservedAtUtc", "oldestBusyObservedAgeMilliseconds" };
        check(!observedJson.Contains(receipt.Message.Text, StringComparison.Ordinal) &&
            !observedJson.Contains(receipt.Message.Id.ToString(), StringComparison.Ordinal) &&
            observedDocument.RootElement.GetProperty("waitingMail").GetProperty("sessions").EnumerateArray()
                .All(row => row.EnumerateObject().All(member => rowMembers.Contains(member.Name))), "WAITING_CONTROL_METADATA_ONLY");
        check(fixture.Runtime.View(receipt).Waits is [{ Reason: "recipient_busy", Source: "directory" }], "WAITING_SENDER_LOCAL_HISTORY");
        check(fixture.Runtime.View(receipt, forPeer: true).Waits is null &&
            !JsonSerializer.Serialize(fixture.Runtime.View(receipt, forPeer: true), ControlProtocol.Json).Contains("waits", StringComparison.Ordinal),
            "WAITING_PEER_HISTORY_OMITTED");
        fixture.Store.RecordRecipientReceipt(receipt.Message.Id, target.Address, fixture.Store.BrokerId, MessageStatus.Delivered);
        var settled = await control.HandleAsync(new("waiting-mail"), default);
        check(settled.WaitingMail?.Sessions.Single(row => row.SessionId == target.Address.SessionId) is { PendingCount: 0, QueuedCount: 0, BusyCount: 0 } &&
            fixture.Runtime.View(receipt).Waits is null, "WAITING_CONTROL_ACK_CLEARS");
        var unknown = await control.HandleAsync(new("unknown-operation"), default);
        check(!unknown.Success && unknown.Code == "unknown_control_operation" && unknown.WaitingMail is null, "WAITING_UNKNOWN_OPERATION_THIRD_OUTCOME");

        await DistinctWireCountsAsync(check);
        await RuntimeLoopAsync(check);
        await RuntimeSharedGateAsync(check);
        await RuntimeWriteFailureAsync(check);
        await RuntimeNonIoFailureAsync(check);
        await RuntimeWriteFailureLoopAsync(check);
    }

    // Unequal values exercise the actual control-row mapping, rather than only its member names.
    private static async Task DistinctWireCountsAsync(Action<bool, string> check)
    {
        var clock = new WaitingClock();
        await using var fixture = new ToolFixture("waiting-mail-wire-counts", clock: clock);
        var sender = fixture.Register("Synthetic distinct-count sender");
        var target = NativeTarget(fixture.Store.BrokerId, clock) with { Activity = SessionActivity.Idle };
        fixture.Store.Register(target);
        var now = clock.GetUtcNow();
        var submitted = fixture.Store.Accept(new(Guid.NewGuid(), Guid.NewGuid(), null, sender.Address, target.Address,
            "Synthetic submitted waiting-count text.", now, now.AddMinutes(30)), fixture.Store.BrokerId);
        var claim = fixture.Store.BeginDelivery();
        check(claim?.Message.Id == submitted.Message.Id, "WAITING_CONTROL_DISTINCT_COUNTS_PRECONDITION");
        fixture.Store.CompleteDelivery(claim!, MessageStatus.Submitted, "synthetic_submitted");
        fixture.Store.Register(target with { Activity = SessionActivity.Busy });
        fixture.Store.Accept(submitted.Message with { Id = Guid.NewGuid(), ConversationId = Guid.NewGuid(), Text = "Synthetic queued waiting-count text." }, fixture.Store.BrokerId);
        fixture.Store.AdmitDueWaitingMailNotices();
        await using var worker = new ControlWorker(fixture);
        await using var control = new LocalControlServer(worker);
        var response = await control.HandleAsync(new("waiting-mail"), default);
        var row = response.WaitingMail?.Sessions.Single(item => item.SessionId == target.Address.SessionId);
        check(response.Success && response.WaitingMail?.BrokerId == fixture.Store.BrokerId &&
            row == new WaitingMailSessionView(target.Address.SessionId, target.Address.Incarnation, 2, 1, 1, 1, 0, now, 0),
            "WAITING_CONTROL_DISTINCT_COUNTS");
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(response, ControlProtocol.Json));
        var mail = document.RootElement.GetProperty("waitingMail");
        var jsonRow = mail.GetProperty("sessions").EnumerateArray().Single(item => item.GetProperty("sessionId").GetString() == target.Address.SessionId);
        check(mail.GetProperty("brokerId").GetGuid() == fixture.Store.BrokerId &&
            mail.GetProperty("observedAtUtc").GetDateTimeOffset() == now &&
            mail.GetProperty("noticeAfterSeconds").GetInt32() == (int)WaitingMailPolicy.NoticeAfter.TotalSeconds &&
            jsonRow.GetProperty("incarnation").GetGuid() == target.Address.Incarnation &&
            jsonRow.GetProperty("pendingCount").GetInt32() == 2 && jsonRow.GetProperty("queuedCount").GetInt32() == 1 &&
            jsonRow.GetProperty("blockedCount").GetInt32() == 1 && jsonRow.GetProperty("busyCount").GetInt32() == 1 &&
            jsonRow.GetProperty("unobservedBlockedCount").GetInt32() == 0 &&
            jsonRow.GetProperty("oldestBusyObservedAtUtc").GetDateTimeOffset() == now &&
            jsonRow.GetProperty("oldestBusyObservedAgeMilliseconds").GetInt64() == 0,
            "WAITING_CONTROL_DISTINCT_JSON_FIELDS");
    }

    // No direct AdmitDue call in this fixture: only Runtime.Start's real delivery loop can admit the notice.
    private static async Task RuntimeLoopAsync(Action<bool, string> check)
    {
        var clock = new WaitingClock();
        var adapter = new WaitingAdapter(clock);
        await using var fixture = new ToolFixture("waiting-mail-runtime-loop", clock: clock, adapters: [adapter]);
        var sender = fixture.Register("Synthetic runtime waiting sender");
        fixture.Store.Register(sender with { LeaseUntil = clock.GetUtcNow().AddMinutes(1) });
        var target = NativeTarget(fixture.Store.BrokerId, clock);
        adapter.Target = target;
        fixture.Store.Register(target);
        var now = clock.GetUtcNow();
        var receipt = fixture.Store.Accept(new(Guid.NewGuid(), Guid.NewGuid(), null, sender.Address, target.Address,
            "Synthetic runtime waiting text.", now, now.AddMinutes(30)), fixture.Store.BrokerId);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        fixture.Runtime.Start(stop.Token);
        try
        {
            bool first = await UntilAsync(() => ObservedAt(fixture.Store, receipt, clock.GetUtcNow()), stop.Token);
            check(first, "WAITING_RUNTIME_LOOP_OBSERVES");
            bool continuous = true;
            for (int tick = 1; tick < 30 && continuous; tick++)
            {
                clock.Advance(TimeSpan.FromSeconds(10));
                Renew(fixture, sender, target, clock);
                continuous = await UntilAsync(() => ObservedAt(fixture.Store, receipt, clock.GetUtcNow()), stop.Token);
            }
            check(continuous && Notices(fixture.Store, sender.Address) == 0, "WAITING_RUNTIME_LOOP_BEFORE_DUE");
            clock.Advance(TimeSpan.FromSeconds(10));
            Renew(fixture, sender, target, clock);
            bool admitted = await UntilAsync(() => Notices(fixture.Store, sender.Address) == 1, stop.Token);
            check(admitted && fixture.Store.FindReceipt(receipt.Message.Id)?.Status == MessageStatus.Accepted &&
                fixture.Store.ReadMessageWait(receipt)?.BusyObservedAgeMilliseconds == 300_000,
                "WAITING_RUNTIME_LOOP_ADMITS_AT_DUE");
            check(adapter.Discoveries > 0 && adapter.Submissions == 0 && !fixture.Runtime.DurableStateUnavailable &&
                !fixture.Runtime.AdapterErrors.ContainsKey("waiting_mail") && !fixture.Runtime.AdapterErrors.ContainsKey("broker") &&
                !fixture.Runtime.AdapterErrors.ContainsKey("delivery"), "WAITING_RUNTIME_LOOP_ISOLATED_NO_SUBMISSION");
        }
        finally
        {
            stop.Cancel();
            await fixture.Runtime.DisposeAsync();
        }
    }

    // The runtime-loop case above proves wiring. This separate two-thread case proves the shared retry gate.
    private static async Task RuntimeSharedGateAsync(Action<bool, string> check)
    {
        var clock = new WaitingClock();
        var fixture = new ToolFixture("waiting-mail-shared-backoff", clock: clock);
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var secondEntered = new ManualResetEventSlim();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        string fault = Path.Combine(fixture.Root, "store", "broker-state.json.next");
        bool ownsFault = false, firstStarted = false, secondStarted = false;
        bool reachedWrite = false, secondBlocked = false, threadsClosed = true, accepted = false, noNotice = false;
        Thread? first = null, second = null;
        Exception? firstFailure = null, secondFailure = null;
        long failedBefore = 0, failedAfter = -1;
        try
        {
            var sender = fixture.Register("Synthetic shared-backoff sender");
            var target = NativeTarget(fixture.Store.BrokerId, clock);
            Renew(fixture, sender, target, clock);
            var now = clock.GetUtcNow();
            var receipt = fixture.Store.Accept(new(Guid.NewGuid(), Guid.NewGuid(), null, sender.Address, target.Address,
                "Synthetic shared-backoff waiting text.", now, now.AddMinutes(30)), fixture.Store.BrokerId);
            fixture.Store.AdmitDueWaitingMailNotices();
            for (int tick = 1; tick < 30; tick++)
            {
                clock.Advance(TimeSpan.FromSeconds(10));
                Renew(fixture, sender, target, clock);
                fixture.Store.AdmitDueWaitingMailNotices();
            }
            var pass = typeof(BrokerRuntime).GetMethod("AdmitWaitingMailPass", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!.CreateDelegate<Action>(fixture.Runtime);
            pass(); // Warm reflection/JIT below the due threshold, before creating the fault or starting either thread.
            clock.Advance(TimeSpan.FromSeconds(10));
            Renew(fixture, sender, target, clock);
            Directory.CreateDirectory(fault); ownsFault = true;
            failedBefore = fixture.Store.CommitTimings().Failed;
            int snapshots = 0;
            fixture.Store.CommitStageProbe = stage =>
            {
                if (stage != "snapshot-write" || Interlocked.Increment(ref snapshots) != 1) return;
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Synthetic held snapshot was not released.");
            };
            first = new Thread(() => { try { pass(); } catch (Exception error) { firstFailure = error; } }) { IsBackground = true };
            second = new Thread(() =>
            {
                secondEntered.Set();
                try { pass(); } catch (Exception error) { secondFailure = error; }
            }) { IsBackground = true };
            first.Start(); firstStarted = true;
            reachedWrite = entered.Wait(TimeSpan.FromSeconds(5));
            if (reachedWrite)
            {
                second.Start(); secondStarted = true;
                secondBlocked = await UntilAsync(() => secondEntered.IsSet &&
                    (second.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, stop.Token);
            }
            release.Set();
            bool firstClosed = !firstStarted || first.Join(TimeSpan.FromSeconds(5));
            bool secondClosed = !secondStarted || second.Join(TimeSpan.FromSeconds(5));
            threadsClosed = firstClosed && secondClosed;
            if (threadsClosed)
            {
                failedAfter = fixture.Store.CommitTimings().Failed;
                noNotice = Notices(fixture.Store, sender.Address) == 0;
                accepted = fixture.Store.FindReceipt(receipt.Message.Id)?.Status == MessageStatus.Accepted;
            }
        }
        finally
        {
            release.Set();
            bool firstClosed = !firstStarted || first!.Join(TimeSpan.FromSeconds(5));
            bool secondClosed = !secondStarted || second!.Join(TimeSpan.FromSeconds(5));
            threadsClosed &= firstClosed && secondClosed;
            if (threadsClosed)
            {
                fixture.Store.CommitStageProbe = null;
                try { if (ownsFault) Directory.Delete(fault, recursive: false); }
                finally
                {
                    try { await fixture.DisposeAsync(); }
                    finally { entered.Dispose(); release.Dispose(); secondEntered.Dispose(); }
                }
            }
            // An unknown-active thread retains its exact owned fixture/events rather than racing Store/scratch disposal.
        }
        check(reachedWrite && secondBlocked && threadsClosed && firstFailure is null && secondFailure is null,
            "WAITING_RUNTIME_BACKOFF_SHARED_GATE_PRECONDITION");
        check(failedBefore == 0 && failedAfter == 1 && noNotice && accepted, "WAITING_RUNTIME_NOTICE_BACKOFF_SHARED_GATE");
    }

    // RuntimeLoopAsync above proves real-loop wiring. This fixture calls the same private pass directly:
    // rate-limited directory observation counts no longer prove passes at a fixed monotonic time.
    private static async Task RuntimeWriteFailureAsync(Action<bool, string> check)
    {
        var clock = new WaitingClock();
        await using var fixture = new ToolFixture("waiting-mail-runtime-write", clock: clock);
        var sender = fixture.Register("Synthetic retry waiting sender");
        var target = NativeTarget(fixture.Store.BrokerId, clock);
        Renew(fixture, sender, target, clock);
        var now = clock.GetUtcNow();
        var receipt = fixture.Store.Accept(new(Guid.NewGuid(), Guid.NewGuid(), null, sender.Address, target.Address,
            "Synthetic retry waiting text.", now, now.AddMinutes(30)), fixture.Store.BrokerId);
        fixture.Store.AdmitDueWaitingMailNotices();
        for (int tick = 1; tick < 30; tick++)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            Renew(fixture, sender, target, clock);
            fixture.Store.AdmitDueWaitingMailNotices();
        }
        var pass = typeof(BrokerRuntime).GetMethod("AdmitWaitingMailPass", System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic)!.CreateDelegate<Action>(fixture.Runtime);
        string fault = Path.Combine(fixture.Root, "store", "broker-state.json.next");
        long failedBefore = fixture.Store.CommitTimings().Failed;
        check(fixture.Store.ReadMessageWait(receipt)?.BusyObservedAgeMilliseconds == 290_000 &&
            Notices(fixture.Store, sender.Address) == 0 && !File.Exists(fault) && !Directory.Exists(fault) &&
            failedBefore == 0, "WAITING_RUNTIME_WRITE_FAILURE_PRECONDITION");
        bool ownsFault = false;
        try
        {
            Directory.CreateDirectory(fault); ownsFault = true;
            clock.Advance(TimeSpan.FromSeconds(10));
            Renew(fixture, sender, target, clock);
            pass();
            bool failed = fixture.Runtime.AdapterErrors.TryGetValue("waiting_mail", out string? error) &&
                error == "waiting_mail_notice_write_failed";
            check(failed && fixture.Store.CommitTimings().Failed == failedBefore + 1 &&
                Notices(fixture.Store, sender.Address) == 0 && !fixture.Runtime.DurableStateUnavailable &&
                !fixture.Runtime.AdapterErrors.ContainsKey("broker") && !fixture.Runtime.AdapterErrors.ContainsKey("delivery") &&
                fixture.Store.FindReceipt(receipt.Message.Id)?.Status == MessageStatus.Accepted,
                "WAITING_RUNTIME_NOTICE_WRITE_FAILURE");
            long firstFailed = fixture.Store.CommitTimings().Failed;
            pass(); // A completed invocation, with no elapsed monotonic time, must not attempt another write.
            bool heldInitially = fixture.Store.CommitTimings().Failed == firstFailed;
            clock.JumpUtc(TimeSpan.FromSeconds(10));
            Renew(fixture, sender, target, clock);
            pass();
            bool heldAfterUtcJump = fixture.Store.CommitTimings().Failed == firstFailed;
            clock.Advance(TimeSpan.FromSeconds(4));
            Renew(fixture, sender, target, clock);
            pass();
            check(heldInitially && heldAfterUtcJump && fixture.Store.CommitTimings().Failed == firstFailed,
                "WAITING_RUNTIME_NOTICE_BACKOFF_SUPPRESSES_WRITES");
            clock.Advance(TimeSpan.FromSeconds(1));
            Renew(fixture, sender, target, clock);
            pass();
            pass(); // All later callers at the retry instant share the newly started backoff.
            check(fixture.Store.CommitTimings().Failed == firstFailed + 1 &&
                fixture.Runtime.AdapterErrors.TryGetValue("waiting_mail", out string? pendingError) && pendingError == "waiting_mail_notice_write_failed" &&
                Notices(fixture.Store, sender.Address) == 0, "WAITING_RUNTIME_NOTICE_BACKOFF_RETRIES_AT_BOUNDARY");
            Directory.Delete(fault, recursive: false); ownsFault = false;
            clock.Advance(TimeSpan.FromSeconds(5));
            Renew(fixture, sender, target, clock);
            pass();
            check(Notices(fixture.Store, sender.Address) == 1 && !fixture.Runtime.AdapterErrors.ContainsKey("waiting_mail") &&
                !fixture.Runtime.DurableStateUnavailable &&
                fixture.Store.FindReceipt(receipt.Message.Id)?.Status == MessageStatus.Accepted,
                "WAITING_RUNTIME_NOTICE_RETRY_CLEARS_ERROR");
        }
        finally
        {
            if (ownsFault) Directory.Delete(fault, recursive: false);
        }
    }

    // Fault-injection contract, not a claim of a reachable non-IO production failure.
    // Use the existing store probe and actual private pass; the runtime loops are deliberately not started.
    private static async Task RuntimeNonIoFailureAsync(Action<bool, string> check)
    {
        var clock = new WaitingClock();
        await using var fixture = new ToolFixture("waiting-mail-runtime-nonio", clock: clock);
        var sender = fixture.Register("Synthetic non-IO waiting sender");
        var target = NativeTarget(fixture.Store.BrokerId, clock);
        Renew(fixture, sender, target, clock);
        var now = clock.GetUtcNow();
        var receipt = fixture.Store.Accept(new(Guid.NewGuid(), Guid.NewGuid(), null, sender.Address, target.Address,
            "Synthetic non-IO waiting text.", now, now.AddMinutes(30)), fixture.Store.BrokerId);
        fixture.Store.AdmitDueWaitingMailNotices();
        for (int tick = 1; tick < 30; tick++)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            Renew(fixture, sender, target, clock);
            fixture.Store.AdmitDueWaitingMailNotices();
        }
        clock.Advance(TimeSpan.FromSeconds(10));
        Renew(fixture, sender, target, clock);
        var pass = typeof(BrokerRuntime).GetMethod("AdmitWaitingMailPass", System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic)!.CreateDelegate<Action>(fixture.Runtime);
        var failure = new InvalidOperationException("Synthetic non-IO waiting notice commit failure.");
        Exception? first = null, second = null;
        int attempts = 0;
        long failedBefore = fixture.Store.CommitTimings().Failed;
        fixture.Store.CommitStageProbe = stage =>
        {
            if (stage != "snapshot-write") return;
            attempts++;
            throw failure;
        };
        try
        {
            try { pass(); } catch (Exception error) { first = error; }
            // No clock advance: a misclassified IO backoff would suppress this second real commit attempt.
            try { pass(); } catch (Exception error) { second = error; }
        }
        finally { fixture.Store.CommitStageProbe = null; }
        bool propagated = ReferenceEquals(first, failure) && ReferenceEquals(second, failure) && attempts == 2 &&
            fixture.Store.CommitTimings().Failed == failedBefore + 2 &&
            !fixture.Runtime.AdapterErrors.ContainsKey("waiting_mail") && !fixture.Runtime.DurableStateUnavailable;
        pass();
        check(propagated && Notices(fixture.Store, sender.Address) == 1 &&
            fixture.Store.FindReceipt(receipt.Message.Id)?.Status == MessageStatus.Accepted &&
            !fixture.Runtime.AdapterErrors.ContainsKey("waiting_mail"), "WAITING_RUNTIME_NONIO_PROPAGATES_WITHOUT_BACKOFF");
    }

    // Keep one real delivery-loop leg for notice IO failure. Commit failure/discovery are the witnesses;
    // throttled directory observation counts are deliberately not used to infer completed runtime passes.
    // This follows the deterministic backoff checks so their established first-failure labels remain unchanged.
    private static async Task RuntimeWriteFailureLoopAsync(Action<bool, string> check)
    {
        var clock = new WaitingClock();
        var adapter = new WaitingAdapter(clock) { BlockAfterFirstDiscovery = true };
        await using var fixture = new ToolFixture("waiting-mail-runtime-write-loop", clock: clock, adapters: [adapter]);
        var sender = fixture.Register("Synthetic loop-write waiting sender");
        var target = NativeTarget(fixture.Store.BrokerId, clock);
        adapter.Target = target;
        Renew(fixture, sender, target, clock);
        var now = clock.GetUtcNow();
        var receipt = fixture.Store.Accept(new(Guid.NewGuid(), Guid.NewGuid(), null, sender.Address, target.Address,
            "Synthetic loop-write waiting text.", now, now.AddMinutes(30)), fixture.Store.BrokerId);
        fixture.Store.AdmitDueWaitingMailNotices();
        for (int tick = 1; tick < 30; tick++)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            Renew(fixture, sender, target, clock);
            fixture.Store.AdmitDueWaitingMailNotices();
        }
        string fault = Path.Combine(fixture.Root, "store", "broker-state.json.next");
        long failedBefore = fixture.Store.CommitTimings().Failed;
        bool ownsFault = false;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            // Discoveries increments inside the stub, before Runtime's successful clear. Observe the actual
            // clear of a seeded value instead, while the notice remains below due and no fault is armed.
            var errors = typeof(BrokerRuntime).GetField("adapterErrors", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)?.GetValue(fixture.Runtime) as
                System.Collections.Concurrent.ConcurrentDictionary<string, string>;
            check(errors is not null,
                "WAITING_RUNTIME_WRITE_FAILURE_LOOP_REFLECTION_PRECONDITION: BrokerRuntime.adapterErrors resolves to the expected dictionary before the runtime starts.");
            errors![adapter.Id] = "synthetic_waiting_startup_clear";
            fixture.Runtime.Start(stop.Token);
            bool firstDiscoveryCompleted = await UntilAsync(() => adapter.Discoveries == 1 &&
                !fixture.Runtime.AdapterErrors.ContainsKey(adapter.Id), stop.Token);
            check(firstDiscoveryCompleted,
                "WAITING_RUNTIME_WRITE_FAILURE_LOOP_FIRST_DISCOVERY_PRECONDITION: the first discovery clears the seeded adapter error before the notice fault is armed.");

            // The stub never completes another preparation pass, so no later successful discovery
            // can erase an adapter error injected by the notice-write mutant.
            Directory.CreateDirectory(fault); ownsFault = true;
            clock.Advance(TimeSpan.FromSeconds(10));
            Renew(fixture, sender, target, clock);
            bool observedFailure = await UntilAsync(() => fixture.Store.CommitTimings().Failed > failedBefore &&
                fixture.Runtime.AdapterErrors.TryGetValue("waiting_mail", out string? code) &&
                code == "waiting_mail_notice_write_failed", stop.Token);
            check(observedFailure,
                "WAITING_RUNTIME_WRITE_FAILURE_LOOP_NOTICE_PRECONDITION: the real delivery loop reaches the armed write fault and records its waiting-mail error within the fixture deadline.");
        }
        finally
        {
            stop.Cancel();
            try { await fixture.Runtime.DisposeAsync(); }
            finally { if (ownsFault) Directory.Delete(fault, recursive: false); }
        }
        check(adapter.Discoveries == 1,
            "WAITING_RUNTIME_WRITE_FAILURE_LOOP_DISCOVERY_BARRIER_PRECONDITION: the adapter completes exactly one discovery so later discovery cannot clear the injected error.");
        // The target stays Busy: zero submissions is a witness of that fixture, not a separate
        // proof that a notice-write fault prevented an otherwise claimable submission.
        // Reflection, discovery progress and barrier failures have their own setup labels above;
        // unexpected setup exceptions retain their original exception and stack instead of becoming this product label.
        check(adapter.Submissions == 0 &&
            !fixture.Runtime.AdapterErrors.ContainsKey(adapter.Id) && !fixture.Runtime.DurableStateUnavailable &&
            !fixture.Runtime.AdapterErrors.ContainsKey("broker") && !fixture.Runtime.AdapterErrors.ContainsKey("delivery") &&
            fixture.Runtime.AdapterErrors.TryGetValue("waiting_mail", out string? finalCode) &&
            finalCode == "waiting_mail_notice_write_failed" && Notices(fixture.Store, sender.Address) == 0 &&
            fixture.Store.FindReceipt(receipt.Message.Id)?.Status == MessageStatus.Accepted,
            "WAITING_RUNTIME_NOTICE_WRITE_FAILURE_ISOLATED_NO_SUBMISSION");
    }

    private static void Renew(ToolFixture fixture, BrokerSession sender, BrokerSession target, WaitingClock clock)
    {
        fixture.Store.Register(sender with { LeaseUntil = clock.GetUtcNow().AddMinutes(1) });
        fixture.Store.Register(target with { LeaseUntil = clock.GetUtcNow().AddMinutes(1) });
    }

    private static BrokerSession NativeTarget(Guid broker, WaitingClock clock) => new(
        new(broker, "claude:" + Guid.NewGuid(), Guid.NewGuid()), "Synthetic runtime Busy", "claude-native",
        SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply, SessionActivity.Busy,
        clock.GetUtcNow().AddMinutes(1));

    private static bool ObservedAt(BrokerStore store, MessageReceipt receipt, DateTimeOffset at) =>
        store.ReadMessageWait(receipt) is { FirstBusyObservedAtUtc: not null } wait &&
        wait.Waits.Any(item => item.Reason == "recipient_busy" && item.Source == "directory" && item.LastObservedAtUtc == at);
    private static int Notices(BrokerStore store, SessionAddress sender) =>
        store.Messages(sender).Count(item => item.Message.Sender.SessionId == BrokerStore.NoticeSessionId);
    private static async Task<bool> UntilAsync(Func<bool> predicate, CancellationToken stop)
    {
        long began = System.Diagnostics.Stopwatch.GetTimestamp();
        while (!stop.IsCancellationRequested && System.Diagnostics.Stopwatch.GetElapsedTime(began) < TimeSpan.FromSeconds(3))
        {
            if (predicate()) return true;
            await Task.Delay(10);
        }
        return false;
    }

    private sealed class WaitingClock : TimeProvider
    {
        private long elapsed, utcOffset;
        private static readonly DateTimeOffset Origin = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref elapsed);
        public override DateTimeOffset GetUtcNow() => Origin.AddTicks(Interlocked.Read(ref elapsed) + Interlocked.Read(ref utcOffset));
        internal void Advance(TimeSpan by) => Interlocked.Add(ref elapsed, by.Ticks);
        internal void JumpUtc(TimeSpan by) => Interlocked.Add(ref utcOffset, by.Ticks);
    }
    private sealed class WaitingAdapter(WaitingClock clock) : ILocalAgentAdapter
    {
        internal BrokerSession? Target;
        internal bool BlockAfterFirstDiscovery { get; init; }
        private int discoveries, submissions;
        internal int Discoveries => Volatile.Read(ref discoveries);
        internal int Submissions => Volatile.Read(ref submissions);
        public string Id => "claude-native";
        // Block before Runtime creates its per-pass discovery deadline. Only the test stop token ends this wait,
        // so a slow scheduler cannot turn the fixture barrier into a synthetic adapter-timeout error.
        public Task PrepareDiscoveryAsync(TimeSpan maximumWait, CancellationToken stop) =>
            BlockAfterFirstDiscovery && Discoveries > 0 ? Task.Delay(Timeout.InfiniteTimeSpan, stop) : Task.CompletedTask;
        public Task<IReadOnlyList<BrokerSession>> DiscoverAsync(Guid brokerId, CancellationToken token)
        {
            Interlocked.Increment(ref discoveries);
            return Task.FromResult<IReadOnlyList<BrokerSession>>([Target! with { LeaseUntil = clock.GetUtcNow().AddMinutes(1) }]);
        }
        public Task<AdapterResult> SubmitAsync(BrokerMessage message, CancellationToken token)
        {
            Interlocked.Increment(ref submissions);
            return Task.FromResult(new AdapterResult(AdapterAdmission.Submitted, "synthetic_unexpected_submission"));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record LegacyResponse(bool Success, string Code, BrokerSnapshot? Snapshot = null,
        LocalConnection? Connection = null, PeerDiagnosticView[]? PeerDiagnostics = null);
    private sealed class ControlWorker : ILocalControlWorker, IAsyncDisposable
    {
        public BrokerRuntime Runtime { get; }
        public WorkerOptions Options { get; }
        public WebApplication Application { get; }
        public string LocalAccessToken => "synthetic-unused";
        public McpStatusView McpStatus => new(false, null, null, []);
        public LanProfileMonitor LanProfile => throw new InvalidOperationException("LAN observation was not requested.");
        internal ControlWorker(ToolFixture fixture)
        {
            Runtime = fixture.Runtime;
            Options = new(fixture.Root, "Synthetic waiting", 0, 0, false) { EnableBrokerDiscovery = false };
            Application = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = fixture.Root }).Build();
        }
        public ValueTask DisposeAsync() => Application.DisposeAsync();
    }
}
