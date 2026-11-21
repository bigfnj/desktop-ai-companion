using System.Security.Cryptography;
using System.Net.Http.Json;
using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Attachments;
using AgentBroker.Worker.Federation;
using AgentBroker.Worker.Security;

// Lane F round 2b: network checks for the defects the round-2 completeness audit confirmed (outbox/003-F-r2-audit-triage.md).
internal static partial class AttachmentFederationChecks
{
    private static async Task RoundTwoBAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        await OneTextDuringPullAsync(scratch, identity, check);
        await OneTextAfterDropAsync(scratch, identity, check);
        await PerPeerSharesAsync(scratch, identity, check);
        await PerPeerDeferralsAsync(scratch, identity, check);
        await SweepWaitsForAdmissionAsync(scratch, identity, check);
        await PrevalidationOnceAsync(scratch, identity, check);
        await CleanupBeforeLookupAsync(scratch, identity, check);
        await RevokedMidBodyAsync(scratch, identity, check);
        await AllowStoppedAsync(scratch, identity, check);
        await ReserveDeferNoGetAsync(scratch, identity, check);
        await PrevalidatedGuardsAsync(scratch, identity, check);
        await SenderBoundNotEarlyAsync(scratch, identity, check);
    }

    // M2: plain text under a message ID whose envelope is still pulling is refused, before any file arrived.
    private static async Task OneTextDuringPullAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var clock = new TestClock();
        var (a, b) = await PairedAsync(scratch, "plain", clock, identity);
        await using (a)
        await using (b)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = new Requests(a) { Behaviour = async (_, _) => { await gate.Task; return false; } };
            var sender = a.Register("att-r2b-plain-sender");
            var recipient = b.Register("att-r2b-plain-recipient");
            var message = SendAttached(a, sender, recipient, Source(scratch, "r2b-plain", "data.txt", "Synthetic pulled content."u8.ToArray()));
            var first = Submit(a, b, message);
            string plain;
            try
            {
                await Until(() => requests.For(message.Id) >= 1, "ATTACHMENT_ONE_TEXT_PLAIN_DURING_PULL: the pull is running", () => "requests " + requests.For(message.Id));
                plain = await Submit(a, b, message with { Text = "Synthetic plain text under the same message ID." });
            }
            finally { gate.TrySetResult(); }
            await first;
            check(plain == AttachmentCodes.EnvelopeInvalid && b.Store.FindReceiptWithText(message.Id)?.Message.Text != "Synthetic plain text under the same message ID.",
                "ATTACHMENT_ONE_TEXT_PLAIN_DURING_PULL: plain text under a message ID whose envelope is still pulling is refused attachment_envelope_invalid");
        }
    }

    // M3: after a transient drop the received folder still holds files for the first envelope; another text is refused.
    private static async Task OneTextAfterDropAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var clock = new TestClock();
        var (a, b) = await PairedAsync(scratch, "drop", clock, identity);
        await using (a)
        await using (b)
        {
            var sender = a.Register("att-r2b-drop-sender");
            var recipient = b.Register("att-r2b-drop-recipient");
            var message = SendAttached(a, sender, recipient, Source(scratch, "r2b-drop", "one.txt", "Synthetic first file."u8.ToArray()),
                Source(scratch, "r2b-drop", "two.txt", "Synthetic second file."u8.ToArray()));
            var requests = new Requests(a)
            {
                Behaviour = (_, context) =>
                {
                    if (!(context.Request.Path.Value ?? "").EndsWith("/1", StringComparison.Ordinal)) return Task.FromResult(false);
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    return Task.FromResult(true);
                }
            };
            string first = await Submit(a, b, message);
            bool firstFileKept = File.Exists(Path.Combine(Folder(b, a, message.Id), "0-one.txt"));
            var swapped = message with { Text = AttachmentEnvelope.Compose("A different synthetic note.", AttachmentEnvelopeOf(message).Attachments) };
            string swap = await Submit(a, b, swapped);
            string plain = await Submit(a, b, message with { Text = "Synthetic plain text under the same message ID." });
            check(first == AttachmentCodes.FetchPending && firstFileKept && swap == AttachmentCodes.EnvelopeInvalid && plain == AttachmentCodes.EnvelopeInvalid &&
                b.Store.FindReceipt(message.Id) is null,
                "ATTACHMENT_ONE_TEXT_AFTER_DROP: after a transient drop, another envelope or plain text under the same message ID is refused while the received folder holds the first envelope's files");
        }
    }

    // M4: each broker has its share of the pull-state and deferral tables; one broker at its share defers only itself.
    private static async Task PerPeerSharesAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var clock = new TestClock();
        var limits = new AttachmentLimits(MaximumPulls: 16, MaximumPullsPerPeer: 16, MaximumPullStates: 64, MaximumDeferrals: 64,
            MaximumPullStatesPerPeer: 2, MaximumDeferralsPerPeer: 64);
        var (a, b) = await PairedAsync(scratch, "shares", clock, identity, recipientLimits: limits);
        await using (a)
        await using (b)
        await using (var c = await Node.Create(Path.Combine(scratch, "att-r2b-shares-c"), clock, identity, attachments: true))
        {
            await Pair(c, b);
            Allow(b, c);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var fromA = new Requests(a) { Behaviour = async (_, _) => { await gate.Task; return false; } };
            var fromC = new Requests(c) { Behaviour = async (_, _) => { await gate.Task; return false; } };
            var senderA = a.Register("att-r2b-shares-a");
            var senderC = c.Register("att-r2b-shares-c");
            var recipient = b.Register("att-r2b-shares-recipient");
            BrokerMessage Message(Node node, BrokerSession sender, string name) =>
                SendAttached(node, sender, recipient, Source(scratch, "r2b-shares", name, "Synthetic shared content."u8.ToArray()));
            var running = new List<Task<string>>();
            try
            {
                var a1 = Message(a, senderA, "a1.txt");
                var a2 = Message(a, senderA, "a2.txt");
                running.Add(Submit(a, b, a1));
                running.Add(Submit(a, b, a2));
                await Until(() => fromA.For(a1.Id) >= 1 && fromA.For(a2.Id) >= 1, "ATTACHMENT_PER_PEER_PULL_STATES: the broker's two pulls hold its share",
                    () => "requests " + fromA.For(a1.Id) + "/" + fromA.For(a2.Id));
                var a3 = Message(a, senderA, "a3.txt");
                string third = await Submit(a, b, a3);
                var c1 = Message(c, senderC, "c1.txt");
                running.Add(Submit(c, b, c1));
                await Until(() => fromC.For(c1.Id) >= 1, "ATTACHMENT_PER_PEER_PULL_STATES: another broker's pull starts", () => "requests " + fromC.For(c1.Id));
                check(third == AttachmentCodes.FetchPending && fromA.For(a3.Id) == 0 && fromC.For(c1.Id) >= 1,
                    "ATTACHMENT_PER_PEER_PULL_STATES: a broker at its share of the pull-state table defers its own next message without a file request, while another broker's pull starts");
            }
            finally
            {
                gate.TrySetResult();
                await Task.WhenAll(running);
            }
        }
    }

    private static async Task PerPeerDeferralsAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var clock = new TestClock();
        var limits = new AttachmentLimits(MaximumPulls: 16, MaximumPullsPerPeer: 16, MaximumPullStates: 64, MaximumDeferrals: 64,
            MaximumPullStatesPerPeer: 16, MaximumDeferralsPerPeer: 2);
        var (a, b) = await PairedAsync(scratch, "deferrals", clock, identity, recipientLimits: limits);
        await using (a)
        await using (b)
        await using (var c = await Node.Create(Path.Combine(scratch, "att-r2b-deferrals-c"), clock, identity, attachments: true))
        {
            await Pair(c, b);
            Allow(b, c);
            var fromA = new Requests(a) { Behaviour = (_, context) => { context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable; return Task.FromResult(true); } };
            var fromC = new Requests(c);
            var senderA = a.Register("att-r2b-deferrals-a");
            var senderC = c.Register("att-r2b-deferrals-c");
            var recipient = b.Register("att-r2b-deferrals-recipient");
            BrokerMessage Message(Node node, BrokerSession sender, string name) =>
                SendAttached(node, sender, recipient, Source(scratch, "r2b-deferrals", name, "Synthetic deferred content."u8.ToArray()));
            var a1 = Message(a, senderA, "a1.txt");
            var a2 = Message(a, senderA, "a2.txt");
            var a3 = Message(a, senderA, "a3.txt");
            string first = await Submit(a, b, a1), second = await Submit(a, b, a2), third = await Submit(a, b, a3);
            var c1 = Message(c, senderC, "c1.txt");
            string other = await Submit(c, b, c1);
            check(first == AttachmentCodes.FetchPending && second == AttachmentCodes.FetchPending && fromA.For(a1.Id) >= 1 && fromA.For(a2.Id) >= 1 &&
                third == AttachmentCodes.FetchPending && fromA.For(a3.Id) == 0 && other == Admitted,
                "ATTACHMENT_PER_PEER_DEFERRALS: a broker at its share of the deferral table defers its own next message without a file request, while another broker's is admitted");
        }
    }

    // Round 2b: a sweep that arrives during admission waits for it, so the message is never admitted with its files gone.
    private static async Task SweepWaitsForAdmissionAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var clock = new TestClock();
        var (a, b) = await PairedAsync(scratch, "gate", clock, identity);
        await using (a)
        await using (b)
        {
            var sender = a.Register("att-r2b-gate-sender");
            var recipient = b.Register("att-r2b-gate-recipient");
            var message = SendAttached(a, sender, recipient, Source(scratch, "r2b-gate", "data.txt", "Synthetic admitted content."u8.ToArray()));
            string file = Path.Combine(Folder(b, a, message.Id), "0-data.txt");
            a.Pairing.BeforeAttachment = async _ => { await Task.Delay(TimeSpan.FromSeconds(6.5)); return false; };
            string first = await Submit(a, b, message);
            await Until(() => File.Exists(file), "ATTACHMENT_SWEEP_WAITS_FOR_ADMISSION: the slow pull publishes its file", () => "first " + first);
            await Task.Delay(500);
            a.Pairing.BeforeAttachment = null;
            clock.Advance(AttachmentStore.OrphanGrace + TimeSpan.FromMinutes(1)); // the folder is old enough for the sweep
            Renew(b, recipient, clock);
            Task? sweep = null;
            bool sweptInside = false;
            b.Federation.BeforeAdmission = () =>
            {
                sweep = Task.Run(() => b.Attachments!.Sweep());
                sweptInside = sweep.Wait(TimeSpan.FromSeconds(1));
            };
            string second = await Submit(a, b, message);
            b.Federation.BeforeAdmission = null;
            if (sweep is not null) await sweep;
            check(first == AttachmentCodes.FetchPending && second == Admitted && !sweptInside && File.Exists(file),
                "ATTACHMENT_SWEEP_WAITS_FOR_ADMISSION: a sweep that starts between the recheck and the admission waits for it, and the admitted message keeps its file");
        }
    }

    // Round 2b: pre-validation runs only before the first pull, so a lease flicker during a running pull does not refuse.
    private static async Task PrevalidationOnceAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var clock = new TestClock();
        var (a, b) = await PairedAsync(scratch, "flicker", clock, identity);
        await using (a)
        await using (b)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = new Requests(a) { Behaviour = async (_, _) => { await gate.Task; return false; } };
            var sender = a.Register("att-r2b-flicker-sender");
            var recipient = b.Register("att-r2b-flicker-recipient");
            var message = SendAttached(a, sender, recipient, Source(scratch, "r2b-flicker", "data.txt", "Synthetic flicker content."u8.ToArray()));
            var first = Submit(a, b, message);
            string during;
            try
            {
                await Until(() => requests.For(message.Id) >= 1, "ATTACHMENT_PREVALIDATION_ONCE: the pull is running", () => "requests " + requests.For(message.Id));
                clock.Advance(TimeSpan.FromSeconds(90)); // the recipient's one-minute lease lapses: not addressable now
                during = await Submit(a, b, message);
            }
            finally { gate.TrySetResult(); }
            await first;
            Renew(b, recipient, clock);
            string after = await Submit(a, b, message);
            if (after == AttachmentCodes.FetchPending) after = await Submit(a, b, message);
            check(during == AttachmentCodes.FetchPending && after == Admitted,
                "ATTACHMENT_PREVALIDATION_ONCE: once a pull runs, a forward during a recipient lease flicker defers instead of being refused, and the message is admitted when the recipient is back");
        }
    }

    // F-1 part two: stale states are cleaned up BEFORE the lookup, so a finished state past its grace is never reused.
    private static async Task CleanupBeforeLookupAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var clock = new TestClock();
        var (a, b) = await PairedAsync(scratch, "cleanup", clock, identity);
        await using (a)
        await using (b)
        {
            var sender = a.Register("att-r2b-cleanup-sender");
            var recipient = b.Register("att-r2b-cleanup-recipient");
            var message = SendAttached(a, sender, recipient, Source(scratch, "r2b-cleanup", "data.txt", "Synthetic cleanup content."u8.ToArray()));
            string file = Path.Combine(Folder(b, a, message.Id), "0-data.txt");
            a.Pairing.BeforeAttachment = async _ => { await Task.Delay(TimeSpan.FromSeconds(6.5)); return false; };
            string first = await Submit(a, b, message);
            await Until(() => File.Exists(file), "ATTACHMENT_CLEANUP_BEFORE_LOOKUP: the slow pull publishes its file", () => "first " + first);
            await Task.Delay(500);
            a.Pairing.BeforeAttachment = null;
            File.Delete(file);
            clock.Advance(PeerFederation.CompletedPullGrace + TimeSpan.FromMinutes(1)); // past the state's grace, well inside the orphan grace
            Renew(b, recipient, clock);
            string second = await Submit(a, b, message);
            check(first == AttachmentCodes.FetchPending && second == Admitted && File.Exists(file),
                "ATTACHMENT_CLEANUP_BEFORE_LOOKUP: a finished state past its grace is removed before the lookup, so the next forward pulls afresh and is admitted on that forward");
        }
    }

    // F-22 split: a revoke that lands after the headers, during the body, is caught by the recheck before publication.
    private static async Task RevokedMidBodyAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var clock = new TestClock();
        var (a, b) = await PairedAsync(scratch, "midbody", clock, identity);
        await using (a)
        await using (b)
        {
            var sender = a.Register("att-r2b-midbody-sender");
            var recipient = b.Register("att-r2b-midbody-recipient");
            var message = SendAttached(a, sender, recipient, Source(scratch, "r2b-midbody", "data.bin", RandomNumberGenerator.GetBytes(256 * 1024)));
            var firstWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            a.Pairing.BeforeAttachment = context =>
            {
                context.Response.Body = new GatedBody(context.Response.Body, firstWrite, gate.Task);
                return Task.FromResult(false);
            };
            long published = b.Attachments!.PublishedFiles;
            var forward = Submit(a, b, message);
            string answer;
            try
            {
                await firstWrite.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await Until(() => Interlocked.Read(ref b.Federation.AttachmentBodyBytesRead) > 0, "ATTACHMENT_REVOKED_MID_BODY: the recipient is reading the body",
                    () => "body bytes read " + Interlocked.Read(ref b.Federation.AttachmentBodyBytesRead));
                b.Federation.RevokePeer(a.Store.BrokerId);
            }
            finally { gate.TrySetResult(); }
            answer = await forward;
            check(answer == AttachmentCodes.RefusedByPolicy && b.Attachments.PublishedFiles == published && !Directory.Exists(Folder(b, a, message.Id)) &&
                b.Store.FindReceipt(message.Id) is null,
                "ATTACHMENT_REVOKED_MID_BODY: a pairing revoked while the file's body is arriving publishes nothing and answers attachments_refused_by_policy");
        }
    }

    // Round 2b: the owner withdraws the allow during the pull (nothing is published) or just before admission (refused).
    private static async Task AllowStoppedAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var clock = new TestClock();
        var (a, b) = await PairedAsync(scratch, "allowoff", clock, identity);
        await using (a)
        await using (b)
        {
            var sender = a.Register("att-r2b-allowoff-sender");
            var recipient = b.Register("att-r2b-allowoff-recipient");
            var message = SendAttached(a, sender, recipient, Source(scratch, "r2b-allowoff", "data.txt", "Synthetic stopped content."u8.ToArray()));
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = new Requests(a) { Behaviour = async (_, _) => { await gate.Task; return false; } };
            long published = b.Attachments!.PublishedFiles;
            var forward = Submit(a, b, message);
            try
            {
                await Until(() => requests.For(message.Id) >= 1, "ATTACHMENT_ALLOW_STOPPED_MID_PULL: the pull is running", () => "requests " + requests.For(message.Id));
                b.Attachments.Policy.Remove(a.Store.BrokerId);
            }
            finally { gate.TrySetResult(); }
            string answer = await forward;
            check(answer == AttachmentCodes.RefusedByPolicy && b.Attachments.PublishedFiles == published && !Directory.Exists(Folder(b, a, message.Id)),
                "ATTACHMENT_ALLOW_STOPPED_MID_PULL: an allow the owner withdraws during the pull publishes nothing more and answers attachments_refused_by_policy");

            Allow(b, a);
            requests.Behaviour = null;
            var later = SendAttached(a, sender, recipient, Source(scratch, "r2b-allowoff", "later.txt", "Synthetic later content."u8.ToArray()));
            b.Federation.BeforeAdmission = () => b.Attachments.Policy.Remove(a.Store.BrokerId);
            string beforeAdmission = await Submit(a, b, later);
            b.Federation.BeforeAdmission = null;
            check(beforeAdmission == AttachmentCodes.RefusedByPolicy && b.Store.FindReceipt(later.Id) is null && !Directory.Exists(Folder(b, a, later.Id)),
                "ATTACHMENT_ALLOW_STOPPED_BEFORE_ADMISSION: an allow withdrawn after the files arrived but before admission refuses the message and keeps nothing");
        }
    }

    // F-5: the route's Defer mapping: a reservation that does not fit this broker's share defers with no file request.
    private static async Task ReserveDeferNoGetAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var clock = new TestClock();
        var (a, b) = await PairedAsync(scratch, "defer", clock, identity, recipientLimits: new(PerPeerInFlightBytes: 3000));
        await using (a)
        await using (b)
        {
            var requests = new Requests(a);
            var sender = a.Register("att-r2b-defer-sender");
            var recipient = b.Register("att-r2b-defer-recipient");
            var message = SendAttached(a, sender, recipient, Source(scratch, "r2b-defer", "data.bin", RandomNumberGenerator.GetBytes(4000)));
            string answer = await Submit(a, b, message);
            check(answer == AttachmentCodes.FetchPending && requests.For(message.Id) == 0 && !Directory.Exists(Folder(b, a, message.Id)),
                "ATTACHMENT_RESERVE_DEFER_NO_GET: a pull that does not fit the broker's in-flight share waits (attachment_fetch_pending) without a file request");
        }
    }

    // F-19: each store refusal the recipient checks before a pull, with no file request.
    private static async Task PrevalidatedGuardsAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var clock = new TestClock();
        var (a, b) = await PairedAsync(scratch, "guards", clock, identity);
        await using (a)
        await using (b)
        {
            var requests = new Requests(a);
            var sender = a.Register("att-r2b-guards-sender");
            var open = b.Register("att-r2b-guards-open");
            var hidden = b.Register("att-r2b-guards-private", privateSession: true);
            var deaf = new BrokerSession(new(b.Store.BrokerId, "att-r2b-guards-deaf", Guid.NewGuid()), "att-r2b-guards-deaf", "synthetic",
                SessionCapabilities.Reply, SessionActivity.Idle, DateTimeOffset.UtcNow.AddMinutes(1));
            b.Store.Register(deaf);
            async Task<(Guid Id, string Answer)> Try(string name, Func<BrokerMessage, BrokerMessage> shape)
            {
                Guid id = Guid.NewGuid();
                string envelope = a.Attachments!.Stage(id, Note, [Source(scratch, "r2b-guards", name, "Synthetic guarded content."u8.ToArray())], out _);
                var now = clock.GetUtcNow();
                var message = shape(new BrokerMessage(id, Guid.NewGuid(), null, sender.Address, open.Address, envelope, now, now.AddMinutes(10)));
                return (id, await Submit(a, b, message));
            }
            bool Untouched(Guid id) => requests.For(id) == 0 && !Directory.Exists(Folder(b, a, id)) && b.Store.FindReceipt(id) is null;
            Guid mismatchId = Guid.NewGuid();
            string mismatchText = a.Attachments!.Stage(mismatchId, Note, [Source(scratch, "r2b-guards", "mismatch.txt", "Synthetic guarded content."u8.ToArray())], out _);
            var mismatchMessage = new BrokerMessage(mismatchId, Guid.NewGuid(), null, new(Guid.NewGuid(), "codex:att-r2b-elsewhere", Guid.NewGuid()),
                open.Address, mismatchText, clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(10));
            string mismatchAnswer;
            using (var raw = b.Client(a.Identity))
            using (var answer = await raw.PostAsJsonAsync("federation/messages", mismatchMessage))
                mismatchAnswer = (int)answer.StatusCode + " " + await answer.Content.ReadAsStringAsync();
            var mismatch = (Id: mismatchId, Answer: mismatchAnswer);
            check(mismatch.Answer.StartsWith("409", StringComparison.Ordinal) && mismatch.Answer.Contains("sender_mismatch", StringComparison.Ordinal) && Untouched(mismatch.Id),
                "ATTACHMENT_PREVALIDATED_SENDER: an envelope whose sender is not on the forwarding broker is refused before any file request");
            var expired = await Try("expired.txt", m => m with { ExpiresAt = clock.GetUtcNow().AddMinutes(-1) });
            check(expired.Answer != Admitted && Untouched(expired.Id),
                "ATTACHMENT_PREVALIDATED_EXPIRED: an envelope already past its deadline is refused before any file request");
            var privateTarget = await Try("private.txt", m => m with { Recipient = hidden.Address });
            check(privateTarget.Answer == "private_session" && Untouched(privateTarget.Id),
                "ATTACHMENT_PREVALIDATED_PRIVATE: an envelope to a private session is refused private_session before any file request");
            var deafTarget = await Try("deaf.txt", m => m with { Recipient = deaf.Address });
            check(deafTarget.Answer == "recipient_cannot_receive" && Untouched(deafTarget.Id),
                "ATTACHMENT_PREVALIDATED_CANNOT_RECEIVE: an envelope to a session that cannot receive is refused before any file request");
            // The transit guard: a session on a third broker. The addressable check behind it would also refuse before any file
            // request, so only the code tells the two apart (audit items-F13-F19). SubmitAsync refuses it client-side; raw POST.
            Guid transitId = Guid.NewGuid();
            string transitText = a.Attachments!.Stage(transitId, Note, [Source(scratch, "r2b-guards", "transit.txt", "Synthetic guarded content."u8.ToArray())], out _);
            var transitMessage = new BrokerMessage(transitId, Guid.NewGuid(), null, sender.Address, new(Guid.NewGuid(), "codex:att-r2b-third-broker", Guid.NewGuid()),
                transitText, clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(10));
            string transitAnswer;
            using (var raw = b.Client(a.Identity))
            using (var answer = await raw.PostAsJsonAsync("federation/messages", transitMessage))
                transitAnswer = (int)answer.StatusCode + " " + await answer.Content.ReadAsStringAsync();
            check(transitAnswer.StartsWith("409", StringComparison.Ordinal) && transitAnswer.Contains("transit_refused", StringComparison.Ordinal) && Untouched(transitId),
                "ATTACHMENT_PREVALIDATED_TRANSIT: an envelope for a session on a third broker is refused transit_refused before any file request");
        }
    }

    // DECISIONS 64: with the sender's own bound live (its runtime carries the attachment store), a slow but serving recipient
    // within its shares is admitted end to end; the sender does not refuse before its own ten minutes.
    private static async Task SenderBoundNotEarlyAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var clock = new TestClock();
        var (a, b) = await PairedAsync(scratch, "slowserve", clock, identity);
        await using (a)
        await using (b)
        {
            var sender = a.Register("att-r2b-slowserve-sender");
            var recipient = b.Register("att-r2b-slowserve-recipient");
            var message = SendAttached(a, sender, recipient, Source(scratch, "r2b-slowserve", "data.txt", "Synthetic slow content."u8.ToArray()));
            string stamp = Path.Combine(a.Attachments!.OutboxFolder(message.Id), AttachmentStore.FetchPendingName);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            a.Pairing.BeforeAttachment = async _ => { await gate.Task; return false; };
            int posts = 0;
            b.Pairing.BeforeMessages = _ => { Interlocked.Increment(ref posts); return Task.FromResult(false); };
            var statuses = new HashSet<MessageStatus>();
            bool stamped = false;
            await using (var runtime = new BrokerRuntime(a.Store, a.Federation, [], null, clock) { Attachments = a.Attachments })
            {
                runtime.Start(default);
                try
                {
                    await Until(() => File.Exists(stamp) && a.Store.FindReceipt(message.Id) is { Status: MessageStatus.Accepted, Reason: AttachmentCodes.FetchPending },
                        "ATTACHMENT_SENDER_BOUND_NOT_EARLY: the held pull defers the first forward and the sender stamps it",
                        () => "sender " + Seen(a, message.Id) + "; stamp " + File.Exists(stamp));
                    stamped = true;
                    // Nine of the sender's ten minutes pass while the recipient is still pulling; at least one more forward is
                    // answered attachment_fetch_pending after that, and the sender must keep the message.
                    clock.Advance(TimeSpan.FromMinutes(9));
                    Renew(b, recipient, clock);
                    a.Store.Register(sender with { LeaseUntil = clock.GetUtcNow().AddMinutes(1) });
                    int postsAtNine = Volatile.Read(ref posts);
                    await Until(() => Volatile.Read(ref posts) >= postsAtNine + 2 || a.Store.FindReceipt(message.Id)?.Status == MessageStatus.Refused,
                        "ATTACHMENT_SENDER_BOUND_NOT_EARLY: a forward after nine minutes is answered and the next one follows",
                        () => "sender " + Seen(a, message.Id) + "; posts since " + (Volatile.Read(ref posts) - postsAtNine));
                }
                finally { gate.TrySetResult(); }
                await Until(() =>
                {
                    var receipt = a.Store.FindReceipt(message.Id);
                    if (receipt is not null) statuses.Add(receipt.Status);
                    return receipt?.Status is MessageStatus.Submitted or MessageStatus.Refused;
                }, "ATTACHMENT_SENDER_BOUND_NOT_EARLY: the message settles once the pull completes", () => "sender " + Seen(a, message.Id));
            }
            check(stamped && a.Store.FindReceipt(message.Id)?.Status == MessageStatus.Submitted && !statuses.Contains(MessageStatus.Refused) &&
                b.Store.FindReceipt(message.Id) is not null,
                "ATTACHMENT_SENDER_BOUND_NOT_EARLY: with the sender's own bound live, a slow but serving recipient within its shares is admitted end to end, nine minutes after the first deferral");
        }
    }

    // A response body that lets the first write through (headers and the first chunk reach the client), then holds every
    // later write until the gate opens.
    private sealed class GatedBody(Stream inner, TaskCompletionSource firstWrite, Task gate) : Stream
    {
        private int writes;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken token) => inner.FlushAsync(token);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count).GetAwaiter().GetResult();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            WriteAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            if (Interlocked.Increment(ref writes) > 1) await gate.WaitAsync(token);
            await inner.WriteAsync(buffer, token);
            if (Volatile.Read(ref writes) == 1)
            {
                await inner.FlushAsync(token);
                firstWrite.TrySetResult();
            }
        }
    }
}
