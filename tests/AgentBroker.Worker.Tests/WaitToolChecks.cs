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

        var waiting = tools.WaitForMessages(owner.Address, default, timeoutSeconds: 20);
        await Task.WhenAny(waiting, Task.Delay(300));
        check(!waiting.IsCompleted, "wait_for_messages blocks while the session has no unacknowledged message");
        Guid first = Guid.NewGuid();
        var elapsed = Stopwatch.StartNew();
        tools.Send(sender.Address, owner.Address, first, "Synthetic message that ends a wait.");
        await Task.WhenAny(waiting, Task.Delay(5000));
        elapsed.Stop();
        check(waiting.IsCompletedSuccessfully && elapsed.Elapsed < TimeSpan.FromSeconds(2) && waiting.Result.Single().Message.Id == first,
            "wait_for_messages returns within two seconds of an arrival");
        var again = await tools.WaitForMessages(owner.Address, default, timeoutSeconds: 20).WaitAsync(TimeSpan.FromSeconds(2));
        check(again.Single().Message.Id == first, "an unacknowledged message is returned again at once by the next wait");
        tools.AcknowledgeMessage(first, owner.Address, MessageStatus.Delivered);
        elapsed.Restart();
        var empty = await tools.WaitForMessages(owner.Address, default, timeoutSeconds: 1);
        elapsed.Stop();
        check(empty.Count == 0 && elapsed.Elapsed >= TimeSpan.FromMilliseconds(900) && elapsed.Elapsed < TimeSpan.FromSeconds(3),
            "wait_for_messages returns empty at its deadline once every message is acknowledged");

        var ordered = new List<Guid>();
        for (int index = 0; index < 3; index++)
        {
            Guid id = Guid.NewGuid(); ordered.Add(id);
            tools.Send(sender.Address, owner.Address, id, "Synthetic ordered message " + index + ".");
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        }
        var limited = await tools.WaitForMessages(owner.Address, default, timeoutSeconds: 0, limit: 2);
        check(limited.Select(receipt => receipt.Message.Id).SequenceEqual(ordered.Take(2)),
            "wait_for_messages returns the oldest unacknowledged messages first up to its limit");
        foreach (Guid id in ordered) tools.AcknowledgeMessage(id, owner.Address, MessageStatus.Delivered);

        using (var cancelFirst = new CancellationTokenSource())
        {
            var holding = tools.WaitForMessages(owner.Address, cancelFirst.Token, timeoutSeconds: 20);
            bool busy = await ToolFixture.RefusedAsync(() => tools.WaitForMessages(owner.Address, default, timeoutSeconds: 0), "wait_in_progress");
            check(busy, "a second concurrent wait for one session is refused as wait_in_progress");
            var beside = await tools.WaitForMessages(sender.Address, default, timeoutSeconds: 0);
            check(beside.Count == 0 && !holding.IsCompleted, "WITNESS another session's wait is admitted beside it");
            elapsed.Restart();
            cancelFirst.Cancel();
            bool cancelled = false;
            try { await holding.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (OperationCanceledException) { cancelled = true; }
            elapsed.Stop();
            check(cancelled && elapsed.Elapsed < TimeSpan.FromSeconds(2), "cancellation ends a wait");
            check((await tools.WaitForMessages(owner.Address, default, timeoutSeconds: 0)).Count == 0,
                "a cancelled wait releases its session for the next wait");
        }

        var crowd = Enumerable.Range(0, 16).Select(index => fixture.Register("Synthetic capacity waiter " + index)).ToArray();
        using (var cancelCrowd = new CancellationTokenSource())
        {
            var holds = crowd.Select(session => tools.WaitForMessages(session.Address, cancelCrowd.Token, timeoutSeconds: 20)).ToArray();
            bool full = await ToolFixture.RefusedAsync(() => tools.WaitForMessages(owner.Address, default, timeoutSeconds: 0), "wait_capacity");
            cancelCrowd.Cancel();
            try { await Task.WhenAll(holds).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { /* Every crowd wait ends cancelled. */ }
            check(full && holds.All(hold => hold.IsCanceled), "a seventeenth concurrent wait is refused as wait_capacity");
            check((await tools.WaitForMessages(owner.Address, default, timeoutSeconds: 0)).Count == 0,
                "WITNESS released wait capacity admits the next wait");
        }

        bool ranges = true;
        foreach (int timeout in new[] { -1, 26 })
        {
            ranges &= await ToolFixture.RefusedAsync(() => tools.WaitForMessages(owner.Address, default, timeoutSeconds: timeout), "timeoutSeconds must be between 0 and 25.");
            ranges &= await ToolFixture.RefusedAsync(() => tools.WaitForReply(first, default, timeoutSeconds: timeout), "timeoutSeconds must be between 0 and 25.");
        }
        foreach (int limit in new[] { 0, 17 })
            ranges &= await ToolFixture.RefusedAsync(() => tools.WaitForMessages(owner.Address, default, timeoutSeconds: 0, limit: limit), "limit must be between 1 and 16.");
        check(ranges, "waits refuse a timeout outside 0..25 and a limit outside 1..16 before waiting");
        var unknown = owner.Address with { SessionId = "mcp:" + Guid.NewGuid().ToString("D") };
        check(await ToolFixture.RefusedAsync(() => tools.WaitForMessages(unknown, default, timeoutSeconds: 1), "session_unavailable"),
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
            await ToolFixture.RefusedAsync(() => tools.WaitForMessages(lost.Address, default, timeoutSeconds: 0), "session_unavailable"),
            "a wait on a restart-lost mailbox with pending messages is refused as session_unavailable, like its acknowledgement");
        check(await ToolFixture.RefusedAsync(() => tools.WaitForReply(lostQuestion, default, timeoutSeconds: 0), "session_unavailable"),
            "a reply wait for a restart-lost sender with a pending reply is refused as session_unavailable");
        fixture.Runtime.RegisterPollingAgent(lostClient, lostIncarnation, "Synthetic restart-lost mailbox", false);
        var recovered = await tools.WaitForMessages(lost.Address, default, timeoutSeconds: 0);
        check(recovered.Select(receipt => receipt.Message.Id).ToHashSet().SetEquals(new[] { kept, lostAnswer }) &&
            tools.AcknowledgeMessage(kept, lost.Address, MessageStatus.Delivered).Status == MessageStatus.Delivered &&
            tools.AcknowledgeMessage(lostAnswer, lost.Address, MessageStatus.Delivered).Status == MessageStatus.Delivered,
            "WITNESS registering the same clientId and incarnation again returns the kept messages, and they acknowledge");

        fixture.Clock.Advance(TimeSpan.FromSeconds(61));
        bool lapsed = fixture.Current(owner.Address)?.Activity == SessionActivity.Away;
        await tools.WaitForMessages(owner.Address, default, timeoutSeconds: 0);
        check(lapsed && fixture.Current(owner.Address) is { Activity: SessionActivity.Idle } back &&
            back.LeaseUntil == fixture.Clock.GetUtcNow().AddMinutes(1),
            "a wait by a retained mailbox restores its presence");

        Guid question = Guid.NewGuid();
        tools.Send(sender.Address, owner.Address, question, "Synthetic question awaiting a reply.");
        var replyWait = tools.WaitForReply(question, default, timeoutSeconds: 20);
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
        var both = await tools.WaitForReply(question, default, timeoutSeconds: 0);
        check(both.Select(receipt => receipt.Message.Id).ToHashSet().SetEquals(new[] { answer, second }),
            "wait_for_reply returns every reply to the original message");
        fixture.Context.Items["agentbroker.identity"] = Guid.NewGuid();
        bool foreign = false;
        try { await tools.WaitForReply(question, default, timeoutSeconds: 0); }
        catch (McpException) { foreign = true; }
        fixture.Context.Items["agentbroker.identity"] = fixture.Store.BrokerId;
        check(foreign && await ToolFixture.RefusedAsync(() => tools.WaitForReply(Guid.NewGuid(), default, timeoutSeconds: 0), "The original message is unavailable."),
            "wait_for_reply refuses another broker's message and a missing original");
    }
}
