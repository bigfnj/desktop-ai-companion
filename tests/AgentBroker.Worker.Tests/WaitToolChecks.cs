using System.Diagnostics;
using AgentBroker.Core;
using ModelContextProtocol;

// Blocking waits replace polling. Every timing here is real (stopwatch), while leases move by the
// manual clock; the bound under test is the change signal, not a sleep.
internal static class WaitToolChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("wait-tools");
        var owner = fixture.Register("Synthetic waiting owner");
        var sender = fixture.Register("Synthetic waiting sender");
        var tools = fixture.Tools;

        var waiting = fixture.NewRequest().Tools.WaitForMessages(owner.Address, default, timeoutSeconds: 20);
        await Task.WhenAny(waiting, Task.Delay(300));
        check(!waiting.IsCompleted, "wait_for_messages blocks while the session has no unacknowledged message");
        Guid first = Guid.NewGuid();
        var elapsed = Stopwatch.StartNew();
        tools.Send(sender.Address, owner.Address, first, "Synthetic message that ends a wait.");
        await Task.WhenAny(waiting, Task.Delay(5000));
        elapsed.Stop();
        check(waiting.IsCompletedSuccessfully && elapsed.Elapsed < TimeSpan.FromSeconds(2) && waiting.Result.Single().Message.Id == first,
            "wait_for_messages returns within two seconds of an arrival");
        var again = await fixture.NewRequest().Tools.WaitForMessages(owner.Address, default, timeoutSeconds: 20).WaitAsync(TimeSpan.FromSeconds(2));
        check(again.Single().Message.Id == first, "an unacknowledged message is returned again at once by the next wait");
        tools.AcknowledgeMessage(first, owner.Address, MessageStatus.Delivered);
        elapsed.Restart();
        var empty = await fixture.NewRequest().Tools.WaitForMessages(owner.Address, default, timeoutSeconds: 1);
        elapsed.Stop();
        check(empty.Count == 0 && elapsed.Elapsed >= TimeSpan.FromMilliseconds(900) && elapsed.Elapsed < TimeSpan.FromSeconds(3),
            "wait_for_messages returns empty at its deadline once every message is acknowledged");
        // Held is an acknowledgement too, and unlike Delivered it keeps its record (no tombstone), so only the wait's own
        // status filter keeps it out (lane broker-next: the filter moved into BrokerStore.AcceptedIncoming, N-broker-e-02).
        Guid held = Guid.NewGuid();
        tools.Send(sender.Address, owner.Address, held, "Synthetic message the owner holds.");
        tools.AcknowledgeMessage(held, owner.Address, MessageStatus.Held);
        var afterHeld = await fixture.NewRequest().Tools.WaitForMessages(owner.Address, default, timeoutSeconds: 0);
        check(afterHeld.Count == 0, "wait_for_messages never returns a message its owner acknowledged as Held");
        tools.AcknowledgeMessage(held, owner.Address, MessageStatus.Delivered);

        var ordered = new List<Guid>();
        for (int index = 0; index < 3; index++)
        {
            Guid id = Guid.NewGuid(); ordered.Add(id);
            tools.Send(sender.Address, owner.Address, id, "Synthetic ordered message " + index + ".");
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        }
        var limited = await fixture.NewRequest().Tools.WaitForMessages(owner.Address, default, timeoutSeconds: 0, limit: 2);
        check(limited.Select(receipt => receipt.Message.Id).SequenceEqual(ordered.Take(2)),
            "wait_for_messages returns the oldest unacknowledged messages first up to its limit");
        foreach (Guid id in ordered) tools.AcknowledgeMessage(id, owner.Address, MessageStatus.Delivered);

        using (var cancelFirst = new CancellationTokenSource())
        {
            var holding = fixture.NewRequest().Tools.WaitForMessages(owner.Address, cancelFirst.Token, timeoutSeconds: 20);
            bool busy = await ToolFixture.RefusedAsync(() => fixture.NewRequest().Tools.WaitForMessages(owner.Address, default, timeoutSeconds: 0), "wait_in_progress");
            check(busy, "a second concurrent wait for one session is refused as wait_in_progress");
            var beside = await fixture.NewRequest().Tools.WaitForMessages(sender.Address, default, timeoutSeconds: 0);
            check(beside.Count == 0 && !holding.IsCompleted, "WITNESS another session's wait is admitted beside it");
            elapsed.Restart();
            cancelFirst.Cancel();
            bool cancelled = false;
            try { await holding.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (OperationCanceledException) { cancelled = true; }
            elapsed.Stop();
            check(cancelled && elapsed.Elapsed < TimeSpan.FromSeconds(2), "cancellation ends a wait");
            check((await fixture.NewRequest().Tools.WaitForMessages(owner.Address, default, timeoutSeconds: 0)).Count == 0,
                "a cancelled wait releases its session for the next wait");
        }

        var crowd = Enumerable.Range(0, 16).Select(index => fixture.Register("Synthetic capacity waiter " + index)).ToArray();
        using (var cancelCrowd = new CancellationTokenSource())
        {
            var holds = crowd.Select(session => fixture.NewRequest().Tools.WaitForMessages(session.Address, cancelCrowd.Token, timeoutSeconds: 20)).ToArray();
            bool full = await ToolFixture.RefusedAsync(() => fixture.NewRequest().Tools.WaitForMessages(owner.Address, default, timeoutSeconds: 0), "wait_capacity");
            cancelCrowd.Cancel();
            try { await Task.WhenAll(holds).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { /* Every crowd wait ends cancelled. */ }
            check(full && holds.All(hold => hold.IsCanceled), "a seventeenth concurrent wait is refused as wait_capacity");
            check((await fixture.NewRequest().Tools.WaitForMessages(owner.Address, default, timeoutSeconds: 0)).Count == 0,
                "WITNESS released wait capacity admits the next wait");
        }

        bool ranges = true;
        foreach (int timeout in new[] { -1, 26 })
        {
            ranges &= await ToolFixture.RefusedAsync(() => fixture.NewRequest().Tools.WaitForMessages(owner.Address, default, timeoutSeconds: timeout), "timeoutSeconds must be between 0 and 25.");
            ranges &= await ToolFixture.RefusedAsync(() => fixture.NewRequest().Tools.WaitForReply(first, default, timeoutSeconds: timeout), "timeoutSeconds must be between 0 and 25.");
        }
        foreach (int limit in new[] { 0, 17 })
            ranges &= await ToolFixture.RefusedAsync(() => fixture.NewRequest().Tools.WaitForMessages(owner.Address, default, timeoutSeconds: 0, limit: limit), "limit must be between 1 and 16.");
        check(ranges, "waits refuse a timeout outside 0..25 and a limit outside 1..16 before waiting");
        var unknown = owner.Address with { SessionId = "mcp:" + Guid.NewGuid().ToString("D") };
        check(await ToolFixture.RefusedAsync(() => fixture.NewRequest().Tools.WaitForMessages(unknown, default, timeoutSeconds: 1), "session_unavailable"),
            "a wait on a mailbox nothing can reach is refused as session_unavailable");

        // N-broker-c-02: a worker restart drops every registration and keeps every message. A wait on that mailbox must
        // say session_unavailable even while messages are pending for it, because acknowledge_message refuses them
        // (recipient_unavailable) until the owner registers the same clientId and incarnation again.
        Guid lostClient = Guid.NewGuid(), lostIncarnation = Guid.NewGuid();
        var lost = fixture.Runtime.RegisterPollingAgent(lostClient, lostIncarnation, "Synthetic restart-lost mailbox", false);
        Guid kept = Guid.NewGuid(), lostQuestion = Guid.NewGuid(), lostAnswer = Guid.NewGuid();
        tools.Send(sender.Address, lost.Address, kept, "Synthetic message kept across a restart.");
        tools.Send(lost.Address, sender.Address, lostQuestion, "Synthetic question from a mailbox about to be lost.");
        tools.ReplyToMessage(lostQuestion, lostAnswer, "Synthetic answer to a mailbox about to be lost.");
        tools.AcknowledgeMessage(lostQuestion, sender.Address, MessageStatus.Delivered);
        fixture.Store.Disconnect(lost.Address);
        check(ToolFixture.Refused(() => tools.AcknowledgeMessage(kept, lost.Address, MessageStatus.Delivered), "recipient_unavailable") &&
            await ToolFixture.RefusedAsync(() => fixture.NewRequest().Tools.WaitForMessages(lost.Address, default, timeoutSeconds: 0), "session_unavailable"),
            "a wait on a restart-lost mailbox with pending messages is refused as session_unavailable, like its acknowledgement");
        check(await ToolFixture.RefusedAsync(() => fixture.NewRequest().Tools.WaitForReply(lostQuestion, default, timeoutSeconds: 0), "session_unavailable"),
            "a reply wait for a restart-lost sender with a pending reply is refused as session_unavailable");
        fixture.Runtime.RegisterPollingAgent(lostClient, lostIncarnation, "Synthetic restart-lost mailbox", false);
        var recovered = await fixture.NewRequest().Tools.WaitForMessages(lost.Address, default, timeoutSeconds: 0);
        check(recovered.Select(receipt => receipt.Message.Id).ToHashSet().SetEquals(new[] { kept, lostAnswer }) &&
            tools.AcknowledgeMessage(kept, lost.Address, MessageStatus.Delivered).Status == MessageStatus.Delivered &&
            tools.AcknowledgeMessage(lostAnswer, lost.Address, MessageStatus.Delivered).Status == MessageStatus.Delivered,
            "WITNESS registering the same clientId and incarnation again returns the kept messages, and they acknowledge");

        fixture.Clock.Advance(TimeSpan.FromSeconds(61));
        bool lapsed = fixture.Current(owner.Address)?.Activity == SessionActivity.Away;
        await fixture.NewRequest().Tools.WaitForMessages(owner.Address, default, timeoutSeconds: 0);
        check(lapsed && fixture.Current(owner.Address) is { Activity: SessionActivity.Idle } back &&
            back.LeaseUntil == fixture.Clock.GetUtcNow().AddMinutes(1),
            "a wait by a retained mailbox restores its presence");

        Guid question = Guid.NewGuid();
        tools.Send(sender.Address, owner.Address, question, "Synthetic question awaiting a reply.");
        var replyWait = fixture.NewRequest().Tools.WaitForReply(question, default, timeoutSeconds: 20);
        await Task.WhenAny(replyWait, Task.Delay(300));
        check(!replyWait.IsCompleted, "wait_for_reply blocks until a reply exists");
        Guid answer = Guid.NewGuid();
        elapsed.Restart();
        tools.ReplyToMessage(question, answer, "Synthetic answer.");
        await Task.WhenAny(replyWait, Task.Delay(5000));
        elapsed.Stop();
        check(replyWait.IsCompletedSuccessfully && elapsed.Elapsed < TimeSpan.FromSeconds(2) &&
            replyWait.Result.Single() is { } reply && reply.Message.Id == answer && reply.Message.ReplyTo == question &&
            reply.Message.Recipient == sender.Address,
            "wait_for_reply returns the reply addressed to the original sender");
        Guid second = Guid.NewGuid();
        tools.ReplyToMessage(question, second, "Synthetic second answer.");
        var both = await fixture.NewRequest().Tools.WaitForReply(question, default, timeoutSeconds: 0);
        check(both.Select(receipt => receipt.Message.Id).ToHashSet().SetEquals(new[] { answer, second }),
            "wait_for_reply returns every reply to the original message");
        tools.AcknowledgeMessage(answer, sender.Address, MessageStatus.Delivered);
        var pendingAnswer = await fixture.NewRequest().Tools.WaitForReply(question, default, timeoutSeconds: 0, unacknowledgedOnly: true);
        check(pendingAnswer.Count == 1 && pendingAnswer[0].Message.Id == second,
            "REPLY_WAIT_PENDING: acknowledged history does not hide the next unacknowledged answer");
        tools.AcknowledgeMessage(second, sender.Address, MessageStatus.Delivered);
        check((await fixture.NewRequest().Tools.WaitForReply(question, default, timeoutSeconds: 0, unacknowledgedOnly: true)).Count == 0,
            "REPLY_WAIT_ACKNOWLEDGED: filtered waits do not immediately return old acknowledged answers");
        fixture.Context.Items["agentbroker.identity"] = Guid.NewGuid();
        bool foreign = false;
        try { await fixture.NewRequest().Tools.WaitForReply(question, default, timeoutSeconds: 0); }
        catch (McpException) { foreign = true; }
        fixture.Context.Items["agentbroker.identity"] = fixture.Store.BrokerId;
        check(foreign && await ToolFixture.RefusedAsync(() => fixture.NewRequest().Tools.WaitForReply(Guid.NewGuid(), default, timeoutSeconds: 0), "The original message is unavailable."),
            "wait_for_reply refuses another broker's message and a missing original");

        await WaitClassesAsync(fixture, check);
        await PendingAddressLossAsync(check);
    }

    private static async Task PendingAddressLossAsync(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("wait-pending-address-loss");
        var owner = fixture.Register("Synthetic pending address owner");
        var sender = fixture.Register("Synthetic pending address sender");
        using var cancel = new CancellationTokenSource();
        var waiting = fixture.NewRequest().Tools.WaitForMessages(owner.Address, cancel.Token, timeoutSeconds: 20);
        bool started = !waiting.IsCompleted && fixture.Runtime.ActiveWaitCount == 1;
        var gate = typeof(BrokerStore).GetField("gate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(fixture.Store)!;
        Guid pending = Guid.NewGuid();
        try
        {
            // The continuation cannot observe the committed message before its owner is disconnected.
            Monitor.Enter(gate);
            try
            {
                fixture.Tools.Send(sender.Address, owner.Address, pending, "Synthetic pending message on an address lost before its next wait pass.");
                fixture.Store.Disconnect(owner.Address);
            }
            finally { Monitor.Exit(gate); }
            bool unavailable = false;
            try { await waiting.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (McpException error) { unavailable = error.Message == "session_unavailable"; }
            check(started && unavailable && fixture.Store.FindReceipt(pending)?.Status == MessageStatus.Accepted &&
                !fixture.Store.IsAddressable(owner.Address) && fixture.Runtime.ActiveWaitCount == 0,
                "WAIT_PENDING_ADDRESS_LOSS: an in-flight wait refuses its disconnected address before returning the newly pending message");
        }
        finally
        {
            cancel.Cancel();
            try { await waiting.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
            catch (McpException) { }
        }
    }

    // Wait classes and the listening count (AgentBroker next campaign, 2026-11-08). A generic mailbox lists as Idle while
    // any of its waits is active and Unknown once the last one ends; a refused wait never changes that.
    private static async Task WaitClassesAsync(ToolFixture fixture, Action<bool, string> check)
    {
        var tools = fixture.Tools;
        SessionActivity? Listed(SessionAddress address) =>
            fixture.Store.PresentedDirectory().SingleOrDefault(session => session.Address == address)?.Activity;
        static async Task Ended(Task wait)
        {
            try { await wait.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { /* A cancelled hold ends cancelled. */ }
            catch (McpException) { /* A refused pass ends the wait too. */ }
        }

        var target = fixture.Register("Synthetic wait-class target");
        var listener = fixture.Register("Synthetic overlapping listener");
        Guid asked = Guid.NewGuid();
        tools.Send(listener.Address, target.Address, asked, "Synthetic message whose receipt the listener waits on.");
        check(Listed(listener.Address) == SessionActivity.Unknown, "WITNESS a mailbox nobody waits on lists as Unknown");

        using (var cancelIncoming = new CancellationTokenSource())
        using (var cancelReceipt = new CancellationTokenSource())
        {
            var incoming = fixture.NewRequest().Tools.WaitForMessages(listener.Address, cancelIncoming.Token, timeoutSeconds: 20);
            var receipt = fixture.NewRequest().Tools.WaitForReceipt(asked, cancelReceipt.Token, timeoutSeconds: 20);
            await Task.WhenAny(incoming, receipt, Task.Delay(300));
            check(!incoming.IsCompleted && !receipt.IsCompleted && Listed(listener.Address) == SessionActivity.Idle,
                "WAIT_CLASSES_OVERLAP: an incoming wait and a receipt wait of one mailbox run together and it lists as Idle");
            cancelReceipt.Cancel();
            await Ended(receipt);
            check(!incoming.IsCompleted && Listed(listener.Address) == SessionActivity.Idle,
                "LISTENING_REFCOUNT: the mailbox stays Idle while its incoming wait continues after its receipt wait ends");
            cancelIncoming.Cancel();
            await Ended(incoming);
            check(Listed(listener.Address) == SessionActivity.Unknown,
                "LISTENING_LAST_WAIT: the mailbox lists as Unknown once its last wait ends");
        }

        // W1 (review 2026-11-08): a wait refused as wait_in_progress neither clears the first wait's listening nor leaves its own.
        using (var cancelFirst = new CancellationTokenSource())
        {
            var first = fixture.NewRequest().Tools.WaitForMessages(listener.Address, cancelFirst.Token, timeoutSeconds: 20);
            bool refused = await ToolFixture.RefusedAsync(() => fixture.NewRequest().Tools.WaitForMessages(listener.Address, default, timeoutSeconds: 0), "wait_in_progress");
            check(refused && !first.IsCompleted && Listed(listener.Address) == SessionActivity.Idle,
                "WAIT_REFUSED_IN_PROGRESS_KEEPS_FIRST: a refused second wait leaves the first wait's mailbox Idle");
            cancelFirst.Cancel();
            await Ended(first);
            check(Listed(listener.Address) == SessionActivity.Unknown,
                "WAIT_REFUSED_IN_PROGRESS_NO_LEAK: after the first wait ends, the refused one has left nothing listening");
        }

        // The capacity counts every class: sixteen receipt waits fill it, and a refused wait leaves its mailbox Unknown.
        var receiptWaiters = Enumerable.Range(0, 16).Select(index => fixture.Register("Synthetic receipt waiter " + index)).ToArray();
        var sent = receiptWaiters.Select(waiter =>
        {
            Guid id = Guid.NewGuid();
            tools.Send(waiter.Address, target.Address, id, "Synthetic message for a capacity receipt wait.");
            return id;
        }).ToArray();
        using (var cancelCrowd = new CancellationTokenSource())
        {
            var holds = sent.Select(id => (Task)fixture.NewRequest().Tools.WaitForReceipt(id, cancelCrowd.Token, timeoutSeconds: 20)).ToArray();
            bool full = await ToolFixture.RefusedAsync(() => fixture.NewRequest().Tools.WaitForMessages(listener.Address, default, timeoutSeconds: 0), "wait_capacity");
            check(full, "WAIT_CAPACITY_COUNTS_RECEIPT: sixteen receipt waits fill the capacity an incoming wait is then refused by");
            check(Listed(listener.Address) == SessionActivity.Unknown,
                "WAIT_REFUSED_CAPACITY_LEAVES_LISTING: a wait refused as wait_capacity leaves its mailbox Unknown");
            cancelCrowd.Cancel();
            foreach (var hold in holds) await Ended(hold);
        }

        // A wait that fails inside its loop (its mailbox lost to a restart) releases its class and its listening.
        Guid lostClient = Guid.NewGuid(), lostIncarnation = Guid.NewGuid();
        var failing = fixture.Runtime.RegisterPollingAgent(lostClient, lostIncarnation, "Synthetic mailbox lost mid-wait", false);
        var doomed = fixture.NewRequest().Tools.WaitForMessages(failing.Address, default, timeoutSeconds: 1);
        fixture.Store.Disconnect(failing.Address);
        bool failed = false;
        try { await doomed.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (McpException error) { failed = error.Message == "session_unavailable"; }
        fixture.Runtime.RegisterPollingAgent(lostClient, lostIncarnation, "Synthetic mailbox lost mid-wait", false);
        check(failed && Listed(failing.Address) == SessionActivity.Unknown &&
            (await fixture.NewRequest().Tools.WaitForMessages(failing.Address, default, timeoutSeconds: 0)).Count == 0,
            "WAIT_RELEASED_ON_FAILURE: a wait that fails mid-loop releases its class and leaves its mailbox Unknown");
    }
}
