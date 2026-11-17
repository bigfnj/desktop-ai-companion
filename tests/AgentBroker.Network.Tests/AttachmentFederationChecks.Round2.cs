using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Attachments;
using AgentBroker.Worker.Federation;
using AgentBroker.Worker.Security;

// Lane F round 2 (003-F-round2.md) between real paired nodes over mutual TLS on loopback. Most checks drive the recipient's
// /federation/messages route directly (PeerFederation.SubmitAsync) so the admission answer itself is observed; the file
// requests each sender served are counted per message ID.
internal static partial class AttachmentFederationChecks
{
    private static async Task RoundTwoAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        await StaleVerifiedAsync(scratch, identity, check);
        await FetchAfterAcknowledgeAsync(scratch, identity, check);
        await QuarantineFullAsync(scratch, identity, check);
        await QuarantineUnavailableAsync(scratch, identity, check);
        await DeferralTableAsync(scratch, identity, check);
        await PullStatesAsync(scratch, identity, check);
        await GetGuardsAsync(scratch, identity, check);
        await RevokeOrderAsync(scratch, identity, check);
        await EnvelopeSwapAsync(scratch, identity, check);
        await BusyStagingAsync(scratch, identity, check);
        await PreValidationAsync(scratch, identity, check);
        await CapacityClockAsync(scratch, identity, check);
        await RevokeMidPullAsync(scratch, identity, check);
        await ServeCapAsync(scratch, identity, check);
        await PullCapsAsync(scratch, identity, check);
        await InvalidEnvelopeAsync(scratch, identity, check);
        await RoundTwoBAsync(scratch, identity, check);
        await RoundSixAsync(scratch, identity, check);
    }

    private const string Admitted = "admitted";

    // The recipient broker's answer to one forward: "admitted", the admission code it refused with, or the failure's type.
    private static async Task<string> Submit(Node sender, Node recipient, BrokerMessage message, CancellationToken token = default)
    {
        try
        {
            var receipt = await sender.Federation.SubmitAsync(TrustOf(sender, recipient), message, token);
            return receipt.Message.Id == message.Id ? Admitted : "other_receipt";
        }
        catch (PeerAdmissionRefusedException refused) { return refused.Code; }
        catch (Exception error) { return error.GetType().Name; }
    }

    // Counts the file requests a sender node serves, per message ID, and lets a check intercept them.
    private sealed class Requests
    {
        private readonly ConcurrentDictionary<Guid, int> counts = new();
        internal Func<Guid, HttpContext, Task<bool>>? Behaviour;
        internal Requests(Node sender) => sender.Pairing.BeforeAttachment = async context =>
        {
            var segments = (context.Request.Path.Value ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
            Guid id = segments.Length >= 3 && Guid.TryParse(segments[2], out Guid parsed) ? parsed : Guid.Empty;
            counts.AddOrUpdate(id, 1, (_, count) => count + 1);
            return Behaviour is { } behaviour && await behaviour(id, context);
        };
        internal int For(Guid id) => counts.GetValueOrDefault(id);
    }

    private static async Task<(Node A, Node B)> PairedAsync(string scratch, string tag, TestClock clock, Func<string, Guid, BrokerIdentity> identity,
        AttachmentLimits? recipientLimits = null, AttachmentLimits? senderLimits = null)
    {
        var a = await Node.Create(Path.Combine(scratch, "att-r2-" + tag + "-a"), clock, identity, attachments: true, attachmentLimits: senderLimits);
        Node? b = null;
        try
        {
            b = await Node.Create(Path.Combine(scratch, "att-r2-" + tag + "-b"), clock, identity, attachments: true, attachmentLimits: recipientLimits);
            await Pair(a, b);
            Allow(b, a);
            return (a, b);
        }
        catch
        {
            if (b is not null) await b.DisposeAsync();
            await a.DisposeAsync();
            throw;
        }
    }

    private static string Folder(Node b, Node a, Guid id) => b.Attachments!.InboxFolder(a.Store.BrokerId, id);

    // A synthetic row's lease is real time; after the test clock moves, the recipient is renewed so the store still takes it.
    private static void Renew(Node b, BrokerSession recipient, TestClock clock) =>
        b.Store.Register(recipient with { LeaseUntil = clock.GetUtcNow().AddMinutes(1) });

    // F-1: a pull finished Verified but its forward had already been answered; the files then vanish (a hand, or the sweep)
    // before the next forward. That forward must pull again, never admit a message whose files are gone.
    private static async Task StaleVerifiedAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var clock = new TestClock();
        var (a, b) = await PairedAsync(scratch, "stale", clock, identity);
        await using (a)
        await using (b)
        {
            var sender = a.Register("att-r2-stale-sender");
            var recipient = b.Register("att-r2-stale-recipient");
            var message = SendAttached(a, sender, recipient, Source(scratch, "r2-stale", "data.txt", "Synthetic stale content."u8.ToArray()));
            string file = Path.Combine(Folder(b, a, message.Id), "0-data.txt");
            a.Pairing.BeforeAttachment = async _ => { await Task.Delay(TimeSpan.FromSeconds(6.5)); return false; };
            string first = await Submit(a, b, message);
            await Until(() => File.Exists(file), "ATTACHMENT_STALE_VERIFIED_REPULLS: the slow pull publishes its verified file after the forward was answered",
                () => "first answer " + first + "; inbox file present " + File.Exists(file));
            await Task.Delay(500); // the pull's task completes right after publication
            a.Pairing.BeforeAttachment = null;
            File.Delete(file);
            string second = await Submit(a, b, message);
            bool neverEmpty = b.Store.FindReceipt(message.Id) is null || File.Exists(file);
            string third = await Submit(a, b, message);
            check(first == AttachmentCodes.FetchPending && second == AttachmentCodes.FetchPending && neverEmpty && third == Admitted &&
                File.Exists(file) && b.Store.FindReceipt(message.Id) is not null,
                "ATTACHMENT_STALE_VERIFIED_REPULLS: a verified pull whose files were removed before admission pulls again; the message is never admitted without its files");

            // The lead's scenario: the sweep removes the unadmitted verified folder past its grace, and the retry pulls afresh.
            var swept = SendAttached(a, sender, recipient, Source(scratch, "r2-stale", "swept.txt", "Synthetic swept content."u8.ToArray()));
            string sweptFile = Path.Combine(Folder(b, a, swept.Id), "0-swept.txt");
            a.Pairing.BeforeAttachment = async _ => { await Task.Delay(TimeSpan.FromSeconds(6.5)); return false; };
            string sweptFirst = await Submit(a, b, swept);
            await Until(() => File.Exists(sweptFile), "ATTACHMENT_SWEPT_PULL_STARTS_AFRESH: the slow pull publishes its verified file",
                () => "first answer " + sweptFirst);
            await Task.Delay(500);
            a.Pairing.BeforeAttachment = null;
            clock.Advance(AttachmentStore.OrphanGrace + TimeSpan.FromMinutes(1));
            Renew(b, recipient, clock);
            b.Attachments!.Sweep();
            bool sweptAway = !Directory.Exists(Folder(b, a, swept.Id));
            string sweptRetry = await Submit(a, b, swept);
            if (sweptRetry == AttachmentCodes.FetchPending) sweptRetry = await Submit(a, b, swept);
            if (!(sweptAway && sweptRetry == Admitted && File.Exists(sweptFile)))
                Console.WriteLine("OBSERVED: ATTACHMENT_SWEPT_PULL_STARTS_AFRESH swept " + sweptAway + "; retry " + sweptRetry + "; file " + File.Exists(sweptFile) +
                    "; first " + sweptFirst);
            check(sweptAway && sweptRetry == Admitted && File.Exists(sweptFile),
                "ATTACHMENT_SWEPT_PULL_STARTS_AFRESH: after the sweep removes an unadmitted verified folder, the next forward pulls afresh and admits with the file present");
        }
    }

    // DECISIONS 49 (F-4 as ruled). The quarantine keeps a manifest per received folder as its record of what arrived; nothing
    // reads it back, because fetch rests on a different invariant: the recipient keeps a paired sender's text after a reply and
    // after acknowledging (BrokerStore.Tombstoned drops text only for messages this broker SENT).
    private static async Task FetchAfterAcknowledgeAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var clock = new TestClock();
        var (a, b) = await PairedAsync(scratch, "ack", clock, identity);
        await using (a)
        await using (b)
        {
            var sender = a.Register("att-r2-ack-sender");
            var recipient = b.Register("att-r2-ack-recipient");
            byte[] content = "Synthetic acknowledged content."u8.ToArray();
            var message = SendAttached(a, sender, recipient, Source(scratch, "r2-ack", "data.txt", content));
            string admitted = await Submit(a, b, message);

            string manifestPath = Path.Combine(Folder(b, a, message.Id), AttachmentStore.ManifestName);
            bool recorded = false;
            if (File.Exists(manifestPath))
            {
                using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
                var root = manifest.RootElement;
                recorded = root.GetProperty("version").GetInt32() == 1 && root.GetProperty("messageId").GetGuid() == message.Id &&
                    root.GetProperty("senderBroker").GetGuid() == a.Store.BrokerId && root.GetProperty("envelope").GetString() == message.Text;
            }
            check(admitted == Admitted && recorded,
                "ATTACHMENT_INBOX_MANIFEST_RECORD: a received folder keeps a manifest naming its message, its sender broker and its exact envelope");

            var reply = b.Store.AcceptReply(message.Id, Guid.NewGuid(), "Synthetic reply to the attachment message.", b.Store.BrokerId);
            bool afterReply = b.Store.FindReceiptWithText(message.Id)?.Message.Text == message.Text;
            b.Store.RecordRecipientReceipt(message.Id, recipient.Address, b.Store.BrokerId, MessageStatus.Delivered);
            bool afterAcknowledge = b.Store.FindReceiptWithText(message.Id)?.Message.Text == message.Text;
            AttachmentView? view = null;
            string? describeError = null;
            try { view = b.Attachments!.Describe(recipient.Address, message.Id, 0); }
            catch (BrokerException error) { describeError = error.Code; }
            bool passed = reply.Message.ReplyTo == message.Id && afterReply && afterAcknowledge && view is { State: "ready" } &&
                view.Path == Path.Combine(Folder(b, a, message.Id), "0-data.txt") && view.Sha256 == Convert.ToHexStringLower(SHA256.HashData(content));
            if (!passed)
                Console.WriteLine("OBSERVED: ATTACHMENT_PAIRED_TEXT_KEPT recipient " + Seen(b, message.Id) + "; text after reply " + afterReply +
                    ", after acknowledge " + afterAcknowledge + "; fetch " + (view?.State ?? describeError));
            check(passed,
                "ATTACHMENT_PAIRED_TEXT_KEPT: a paired sender's message keeps its text at the recipient after a reply and after acknowledge Delivered, so fetch still finds its file");
        }
    }

    // F-5 (DECISIONS 39): admitted files alone leave no room, so the next envelope is refused before any file request.
    private static async Task QuarantineFullAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var clock = new TestClock();
        var (a, b) = await PairedAsync(scratch, "full", clock, identity, recipientLimits: new(QuarantineBytes: 6000));
        await using (a)
        await using (b)
        {
            var requests = new Requests(a);
            var sender = a.Register("att-r2-full-sender");
            var recipient = b.Register("att-r2-full-recipient");
            var first = SendAttached(a, sender, recipient, Source(scratch, "r2-full", "first.bin", RandomNumberGenerator.GetBytes(4000)));
            var second = SendAttached(a, sender, recipient, Source(scratch, "r2-full", "second.bin", RandomNumberGenerator.GetBytes(4000)));
            string admitted = await Submit(a, b, first);
            string refused = await Submit(a, b, second);
            check(admitted == Admitted && refused == AttachmentCodes.QuarantineFull && requests.For(second.Id) == 0 &&
                b.Store.FindReceipt(second.Id) is null && !Directory.Exists(Folder(b, a, second.Id)),
                "ATTACHMENT_QUARANTINE_FULL_NO_GET: with admitted files filling the quarantine an envelope is refused attachment_quarantine_full before any file request");
        }
    }

    // F-6: a quarantine that cannot hold files (seam: publication throws InvalidDataException) is a definite answer at once.
    private static async Task QuarantineUnavailableAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var clock = new TestClock();
        var (a, b) = await PairedAsync(scratch, "unavail", clock, identity);
        await using (a)
        await using (b)
        {
            var sender = a.Register("att-r2-unavail-sender");
            var recipient = b.Register("att-r2-unavail-recipient");
            var message = SendAttached(a, sender, recipient, Source(scratch, "r2-unavail", "data.txt", "Synthetic content."u8.ToArray()));
            b.Attachments!.PublishFault = () => new InvalidDataException("Synthetic quarantine on a volume that is not NTFS.");
            string answer = await Submit(a, b, message);
            check(answer == AttachmentCodes.QuarantineUnavailable && b.Attachments.View().QuarantineUnavailable == true &&
                b.Store.FindReceipt(message.Id) is null,
                "ATTACHMENT_QUARANTINE_UNAVAILABLE: a quarantine that cannot hold files answers attachment_quarantine_unavailable on the first forward, and the recipient's status shows it");
        }
    }

    // F-7 and T-4: the deferral table never evicts an entry younger than the bound; when it is full a new message waits
    // without a file request, and the oldest entry still ends at its own bound.
    private static async Task DeferralTableAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var clock = new TestClock();
        var limits = new AttachmentLimits(MaximumPulls: 16, MaximumPullsPerPeer: 16, MaximumPullStates: 16, MaximumDeferrals: 4);
        var (a, b) = await PairedAsync(scratch, "table", clock, identity, recipientLimits: limits);
        await using (a)
        await using (b)
        {
            var requests = new Requests(a) { Behaviour = (_, context) => { context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable; return Task.FromResult(true); } };
            var sender = a.Register("att-r2-table-sender");
            var recipient = b.Register("att-r2-table-recipient");
            var messages = Enumerable.Range(0, 5).Select(index =>
                SendAttached(a, sender, recipient, Source(scratch, "r2-table", "data" + index + ".txt", "Synthetic table content."u8.ToArray()))).ToArray();
            var answers = new List<string>();
            foreach (var message in messages) answers.Add(await Submit(a, b, message));
            clock.Advance(PeerFederation.AttachmentDeferralBound + TimeSpan.FromMinutes(1));
            Renew(b, recipient, clock);
            string oldest = await Submit(a, b, messages[0]);
            check(answers.All(answer => answer == AttachmentCodes.FetchPending) && requests.For(messages[4].Id) == 0 &&
                messages.Take(4).All(message => requests.For(message.Id) >= 1) && oldest == AttachmentCodes.FetchFailed,
                "ATTACHMENT_DEFERRAL_TABLE_NO_EVICTION: with the deferral table full a new message waits without a file request, and the oldest entry keeps its bound");
        }
    }

    // F-8: forwards aborted during the inline wait leave finished pull states behind; they stay under the cap and go once
    // their grace passes.
    private static async Task PullStatesAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var clock = new TestClock();
        var limits = new AttachmentLimits(MaximumPulls: 16, MaximumPullsPerPeer: 16, MaximumPullStates: 8, MaximumPullStatesPerPeer: 64,
            MaximumDeferralsPerPeer: 64);
        var (a, b) = await PairedAsync(scratch, "states", clock, identity, recipientLimits: limits);
        await using (a)
        await using (b)
        {
            var sender = a.Register("att-r2-states-sender");
            var recipient = b.Register("att-r2-states-recipient");
            a.Pairing.BeforeAttachment = async _ => { await Task.Delay(TimeSpan.FromSeconds(2)); return false; };
            string source = Source(scratch, "r2-states", "data.txt", "Synthetic aborted content."u8.ToArray());
            int most = 0;
            for (int index = 0; index < 50; index++)
            {
                var message = SendAttached(a, sender, recipient, source);
                using var abort = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
                await Submit(a, b, message, abort.Token);
                most = Math.Max(most, b.Federation.AttachmentPullStateCount);
            }
            await Task.Delay(TimeSpan.FromSeconds(3)); // every started pull finishes; nothing forwards them again
            int finished = b.Federation.AttachmentPullStateCount;
            a.Pairing.BeforeAttachment = null;
            var last = SendAttached(a, sender, recipient, source);
            clock.Advance(PeerFederation.CompletedPullGrace + TimeSpan.FromMinutes(1));
            Renew(b, recipient, clock);
            string admitted = await Submit(a, b, last);
            int after = b.Federation.AttachmentPullStateCount;
            await Task.Delay(200); // the last pull's linked source is disposed by its completion continuation
            int linked = Volatile.Read(ref b.Federation.AttachmentLinkedSourcesLive);
            bool bounded = most <= limits.MaximumPullStates && finished >= 1 && admitted == Admitted && after == 0 && linked == 0;
            if (!bounded)
                Console.WriteLine("OBSERVED: ATTACHMENT_PULL_STATES_BOUNDED most " + most + ", finished " + finished + ", last " + admitted + ", after " + after +
                    ", linked sources live " + linked);
            check(bounded,
                "ATTACHMENT_PULL_STATES_BOUNDED: 50 aborted forwards never hold more than the pull-state cap, finished states go once their grace passes, and every linked source is released");
        }
    }

    // F-10: each guard of the file endpoint, with every other condition valid, answers 404.
    private static async Task GetGuardsAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var clock = new TestClock();
        var (a, b) = await PairedAsync(scratch, "get", clock, identity);
        await using (a)
        await using (b)
        {
            var sender = a.Register("att-r2-get-sender");
            var recipient = b.Register("att-r2-get-recipient");
            using var client = a.Client(b.Identity);
            async Task<HttpStatusCode> Get(Guid id, int index)
            {
                using var answer = await client.GetAsync("federation/attachments/" + id.ToString("D") + "/" + index, HttpCompletionOption.ResponseHeadersRead);
                return answer.StatusCode;
            }
            BrokerMessage Staged(string name) => SendAttached(a, sender, recipient, Source(scratch, "r2-get", name, RandomNumberGenerator.GetBytes(1024)));

            // Status: the only message, so the store's next claim is this one; Submitted keeps the staging (DECISIONS 38).
            var submitted = Staged("submitted.bin");
            var claim = a.Store.BeginDeliveryClaim();
            bool claimed = claim?.Message.Id == submitted.Id && a.Store.TryCompleteDelivery(claim!, MessageStatus.Submitted, "peer_receipt", out _);
            check(claimed && Directory.Exists(a.Attachments!.OutboxFolder(submitted.Id)) && await Get(submitted.Id, 0) == HttpStatusCode.NotFound,
                "ATTACHMENT_GET_STATUS: a file request for a message no longer Accepted or Submitting is 404, though its staging is kept");

            var control = Staged("control.bin");
            bool served = await Get(control.Id, 0) == HttpStatusCode.OK;
            check(served && await Get(control.Id, 1) == HttpStatusCode.NotFound,
                "ATTACHMENT_GET_INDEX: a file request for an index the envelope does not have is 404 (the same message's index 0 is served)");

            var rewritten = Staged("rewritten.bin");
            string other = AttachmentEnvelope.Compose("A different synthetic note.", AttachmentEnvelopeOf(rewritten).Attachments);
            File.WriteAllText(Path.Combine(a.Attachments!.OutboxFolder(rewritten.Id), AttachmentStore.ManifestName),
                JsonSerializer.Serialize(new { version = 1, messageId = rewritten.Id, envelope = other }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            check(await Get(rewritten.Id, 0) == HttpStatusCode.NotFound,
                "ATTACHMENT_GET_MANIFEST: a staging whose manifest no longer matches the message text is 404");

            var truncated = Staged("truncated.bin");
            File.WriteAllBytes(Path.Combine(a.Attachments!.OutboxFolder(truncated.Id), "0-truncated.bin"), new byte[100]);
            check(await Get(truncated.Id, 0) == HttpStatusCode.NotFound,
                "ATTACHMENT_GET_LENGTH: a staged file whose length is not the declared size is 404");

            var expired = Staged("expired.bin");
            clock.Advance(TimeSpan.FromMinutes(61));
            check(a.Store.FindReceiptWithText(expired.Id) is { Status: MessageStatus.Accepted } && await Get(expired.Id, 0) == HttpStatusCode.NotFound,
                "ATTACHMENT_GET_EXPIRED: a file request for a message past its deadline is 404");
        }

        var offClock = new TestClock();
        await using var off = await Node.Create(Path.Combine(scratch, "att-r2-get-off-a"), offClock, identity, attachments: false);
        await using var peer = await Node.Create(Path.Combine(scratch, "att-r2-get-off-b"), offClock, identity, attachments: true);
        await Pair(off, peer);
        var offSender = off.Register("att-r2-get-off-sender");
        var offRecipient = peer.Register("att-r2-get-off-recipient");
        var staged = SendAttached(off, offSender, offRecipient, Source(scratch, "r2-get-off", "data.bin", RandomNumberGenerator.GetBytes(512)));
        using (var offClient = off.Client(peer.Identity))
        using (var answer = await offClient.GetAsync("federation/attachments/" + staged.Id.ToString("D") + "/0"))
            check(answer.StatusCode == HttpStatusCode.NotFound,
                "ATTACHMENT_GET_DISABLED: a sender broker with attachments off serves no file, even one it staged");
    }

    private static AttachmentEnvelopeData AttachmentEnvelopeOf(BrokerMessage message) =>
        AttachmentEnvelope.TryParse(message.Text, out var envelope) == AttachmentParse.Valid ? envelope! : throw new InvalidOperationException("not an envelope");

    // F-14: the allow goes BEFORE the trust, so a failed sidecar write leaves the broker paired and the revoke retryable; a
    // broker paired again starts denied.
    private static async Task RevokeOrderAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var clock = new TestClock();
        var (a, b) = await PairedAsync(scratch, "revoke", clock, identity);
        await using (a)
        await using (b)
        {
            b.Attachments!.Policy.WriteFault = () => new IOException("Synthetic sidecar write failure.");
            bool threw = false;
            try { b.Federation.RevokePeer(a.Store.BrokerId); }
            catch (IOException) { threw = true; }
            bool stillPaired = b.Store.Peers().Any(peer => peer.BrokerId == a.Store.BrokerId);
            b.Attachments.Policy.WriteFault = null;
            b.Federation.RevokePeer(a.Store.BrokerId);
            bool revoked = b.Store.Peers().All(peer => peer.BrokerId != a.Store.BrokerId);
            check(threw && stillPaired && revoked,
                "ATTACHMENT_REVOKE_POLICY_FIRST: a revoke whose allow removal fails leaves the broker paired, and the owner's retry revokes it");
            a.Federation.RevokePeer(b.Store.BrokerId);
            await Pair(a, b);
            var sender = a.Register("att-r2-revoke-sender");
            var recipient = b.Register("att-r2-revoke-recipient");
            var message = SendAttached(a, sender, recipient, Source(scratch, "r2-revoke", "data.txt", "Synthetic re-paired content."u8.ToArray()));
            check(await Submit(a, b, message) == AttachmentCodes.RefusedByPolicy,
                "ATTACHMENT_REPAIR_STARTS_DENIED: a broker paired again after a revoke is refused by policy until the owner allows it again");
        }
    }

    // F-15: one message ID carries one envelope; a different text under the same ID during its pull is refused.
    private static async Task EnvelopeSwapAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var clock = new TestClock();
        var (a, b) = await PairedAsync(scratch, "swap", clock, identity);
        await using (a)
        await using (b)
        {
            var sender = a.Register("att-r2-swap-sender");
            var recipient = b.Register("att-r2-swap-recipient");
            var message = SendAttached(a, sender, recipient, Source(scratch, "r2-swap", "data.txt", "Synthetic swap content."u8.ToArray()));
            var swapped = message with { Text = AttachmentEnvelope.Compose("A different synthetic note.", AttachmentEnvelopeOf(message).Attachments) };
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            a.Pairing.BeforeAttachment = async _ => { await release.Task; return false; };
            string first, second;
            try
            {
                first = await Submit(a, b, message);
                second = await Submit(a, b, swapped);
            }
            finally { release.TrySetResult(); }
            await Until(() => File.Exists(Path.Combine(Folder(b, a, message.Id), "0-data.txt")),
                "ATTACHMENT_ONE_ENVELOPE_PER_ID: the original pull publishes its file", () => "first " + first + "; second " + second);
            await Task.Delay(500);
            a.Pairing.BeforeAttachment = null;
            string third = await Submit(a, b, message);
            check(first == AttachmentCodes.FetchPending && second == AttachmentCodes.EnvelopeInvalid && third == Admitted &&
                b.Store.FindReceiptWithText(message.Id)?.Message.Text == message.Text,
                "ATTACHMENT_ONE_ENVELOPE_PER_ID: a second submission of the same message ID with a different envelope during its pull is refused attachment_envelope_invalid; the original is admitted");
        }
    }

    // F-16: a staged file the sender cannot open now (held FileShare.None) answers 503: a deferral, then admission.
    private static async Task BusyStagingAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var clock = new TestClock();
        var (a, b) = await PairedAsync(scratch, "busy", clock, identity);
        await using (a)
        await using (b)
        {
            var sender = a.Register("att-r2-busy-sender");
            var recipient = b.Register("att-r2-busy-recipient");
            var message = SendAttached(a, sender, recipient, Source(scratch, "r2-busy", "data.txt", "Synthetic busy content."u8.ToArray()));
            string staged = Path.Combine(a.Attachments!.OutboxFolder(message.Id), "0-data.txt");
            var held = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.None);
            HttpStatusCode direct;
            using (var client = a.Client(b.Identity))
            using (var answer = await client.GetAsync("federation/attachments/" + message.Id.ToString("D") + "/0"))
                direct = answer.StatusCode;
            bool sawDeferral = false;
            try
            {
                await using var runtime = new BrokerRuntime(a.Store, a.Federation, []);
                runtime.Start(default);
                await Until(() =>
                {
                    sawDeferral |= a.Store.FindReceipt(message.Id) is { Status: MessageStatus.Accepted, Reason: AttachmentCodes.FetchPending };
                    if (sawDeferral && held is not null) { held.Dispose(); held = null!; }
                    return a.Store.FindReceipt(message.Id)?.Status is MessageStatus.Submitted or MessageStatus.Refused;
                }, "ATTACHMENT_STAGED_FILE_BUSY_DEFERS: the message whose staged file was held settles",
                    () => "sender " + Seen(a, message.Id) + "; deferral seen " + sawDeferral + "; direct request " + (int)direct);
            }
            finally { held?.Dispose(); }
            check(direct == HttpStatusCode.ServiceUnavailable && sawDeferral && a.Store.FindReceipt(message.Id)?.Status == MessageStatus.Submitted &&
                b.Store.FindReceipt(message.Id) is not null,
                "ATTACHMENT_STAGED_FILE_BUSY_DEFERS: a staged file the sender cannot open now answers 503, the message defers, and it is admitted once the file is free");
        }
    }

    // F-19: what the store would refuse is refused before a single file request.
    private static async Task PreValidationAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var clock = new TestClock();
        var (a, b) = await PairedAsync(scratch, "prevalid", clock, identity);
        await using (a)
        await using (b)
        {
            var requests = new Requests(a);
            var sender = a.Register("att-r2-prevalid-sender");
            Guid id = Guid.NewGuid();
            string envelope = a.Attachments!.Stage(id, Note, [Source(scratch, "r2-prevalid", "data.txt", "Synthetic content."u8.ToArray())], out _);
            var now = clock.GetUtcNow();
            var message = new BrokerMessage(id, Guid.NewGuid(), null, sender.Address, new(b.Store.BrokerId, "codex:att-r2-nobody", Guid.NewGuid()),
                envelope, now, now.AddMinutes(10));
            string answer = await Submit(a, b, message);
            check(answer == "recipient_unavailable" && requests.For(id) == 0 && !Directory.Exists(Folder(b, a, id)),
                "ATTACHMENT_PREVALIDATED_NO_PULL: an envelope to a recipient the store would refuse is refused recipient_unavailable with no file request");
        }
    }

    // F-21: the bound's clock starts at the first ACTUAL pull, not at a capacity wait.
    private static async Task CapacityClockAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var clock = new TestClock();
        var (a, b) = await PairedAsync(scratch, "capclock", clock, identity, recipientLimits: new(MaximumPulls: 1, MaximumPullsPerPeer: 1));
        await using (a)
        await using (b)
        {
            var sender = a.Register("att-r2-capclock-sender");
            var recipient = b.Register("att-r2-capclock-recipient");
            var holder = SendAttached(a, sender, recipient, Source(scratch, "r2-capclock", "holder.txt", "Synthetic holder content."u8.ToArray()));
            var waiting = SendAttached(a, sender, recipient, Source(scratch, "r2-capclock", "waiting.txt", "Synthetic waiting content."u8.ToArray()));
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = new Requests(a)
            {
                Behaviour = async (id, context) =>
                {
                    if (id == holder.Id) { await gate.Task; return false; }
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    return true;
                }
            };
            var holding = Submit(a, b, holder);
            await Until(() => requests.For(holder.Id) >= 1, "ATTACHMENT_BOUND_FROM_FIRST_PULL: the holder's pull takes the only pull slot",
                () => "holder requests " + requests.For(holder.Id));
            string capacity = await Submit(a, b, waiting);
            clock.Advance(PeerFederation.AttachmentDeferralBound + TimeSpan.FromMinutes(1));
            Renew(b, recipient, clock);
            string stillCapacity = await Submit(a, b, waiting);
            int requestsWhileWaiting = requests.For(waiting.Id);
            gate.SetResult();
            await holding;
            await Until(() => b.Federation.AttachmentPullStateCount == 0 || File.Exists(Path.Combine(Folder(b, a, holder.Id), "0-holder.txt")),
                "ATTACHMENT_BOUND_FROM_FIRST_PULL: the holder's pull finishes", () => "pull states " + b.Federation.AttachmentPullStateCount);
            await Task.Delay(300);
            string firstPull = await Submit(a, b, waiting);
            clock.Advance(PeerFederation.AttachmentDeferralBound + TimeSpan.FromMinutes(1));
            Renew(b, recipient, clock);
            string bound = await Submit(a, b, waiting);
            check(capacity == AttachmentCodes.FetchPending && stillCapacity == AttachmentCodes.FetchPending && requestsWhileWaiting == 0 &&
                firstPull == AttachmentCodes.FetchPending && requests.For(waiting.Id) >= 1 && bound == AttachmentCodes.FetchFailed,
                "ATTACHMENT_BOUND_FROM_FIRST_PULL: capacity waits start no clock (11 min of them still defer); the bound runs from the first actual pull");
        }
    }

    // F-22: the pairing ends during the pull; nothing is admitted and the sender ends Refused by policy, not Uncertain.
    private static async Task RevokeMidPullAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var clock = new TestClock();
        var (a, b) = await PairedAsync(scratch, "midrevoke", clock, identity);
        await using (a)
        await using (b)
        {
            var sender = a.Register("att-r2-midrevoke-sender");
            var recipient = b.Register("att-r2-midrevoke-recipient");
            var message = SendAttached(a, sender, recipient, Source(scratch, "r2-midrevoke", "data.txt", "Synthetic revoked content."u8.ToArray()));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            a.Pairing.BeforeAttachment = async _ => { entered.TrySetResult(); await gate.Task; return false; };
            CountingHandler? counter = null;
            b.Federation.AttachmentHandler = inner => counter = new CountingHandler(inner);
            var forward = Submit(a, b, message);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            b.Federation.RevokePeer(a.Store.BrokerId);
            gate.SetResult();
            string answer = await forward;
            check(answer == AttachmentCodes.RefusedByPolicy && !Directory.Exists(Folder(b, a, message.Id)) && b.Store.FindReceipt(message.Id) is null &&
                counter is not null && Interlocked.Read(ref counter.BodyBytes) == 0,
                "ATTACHMENT_REVOKED_MID_PULL: a pairing revoked before the file's answer arrives admits nothing, reads no body byte, keeps no file, and answers attachments_refused_by_policy");
        }
    }

    // T-4: the per-broker serve cap: while two of a broker's file requests are being served, its third is answered 429.
    private static async Task ServeCapAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var clock = new TestClock();
        var (a, b) = await PairedAsync(scratch, "serve", clock, identity);
        await using (a)
        await using (b)
        {
            var sender = a.Register("att-r2-serve-sender");
            var recipient = b.Register("att-r2-serve-recipient");
            var message = SendAttached(a, sender, recipient, Source(scratch, "r2-serve", "data.bin", RandomNumberGenerator.GetBytes(4096)));
            string route = "federation/attachments/" + message.Id.ToString("D") + "/0";
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int started = 0;
            // Response.OnStarting runs inside the endpoint, after it entered the serve count; the first two wait there.
            a.Pairing.BeforeResponse = async context =>
            {
                if (context.Request.Path.StartsWithSegments("/federation/attachments") && Interlocked.Increment(ref started) <= 2) await gate.Task;
            };
            using var client = a.Client(b.Identity);
            var first = client.GetAsync(route, HttpCompletionOption.ResponseHeadersRead);
            var second = client.GetAsync(route, HttpCompletionOption.ResponseHeadersRead);
            await Until(() => Volatile.Read(ref started) >= 2, "ATTACHMENT_SERVE_CAP: two file requests are being served",
                () => "requests started " + Volatile.Read(ref started));
            HttpStatusCode third;
            using (var answer = await client.GetAsync(route)) third = answer.StatusCode;
            gate.SetResult();
            using var one = await first;
            using var two = await second;
            check(third == (HttpStatusCode)429 && one.StatusCode == HttpStatusCode.OK && two.StatusCode == HttpStatusCode.OK,
                "ATTACHMENT_SERVE_CAP: a broker's third concurrent file request is answered 429 while two are being served");
        }
    }

    // T-4: the pull caps: a broker's third concurrent pull and the pool's fourth wait, each without a file request.
    private static async Task PullCapsAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var clock = new TestClock();
        var (a, b) = await PairedAsync(scratch, "caps", clock, identity, recipientLimits: new(MaximumPulls: 3, MaximumPullsPerPeer: 2));
        await using (a)
        await using (b)
        await using (var c = await Node.Create(Path.Combine(scratch, "att-r2-caps-c"), clock, identity, attachments: true))
        {
            await Pair(c, b);
            Allow(b, c);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var fromA = new Requests(a) { Behaviour = async (_, _) => { await gate.Task; return false; } };
            var fromC = new Requests(c) { Behaviour = async (_, _) => { await gate.Task; return false; } };
            var senderA = a.Register("att-r2-caps-sender-a");
            var senderC = c.Register("att-r2-caps-sender-c");
            var recipient = b.Register("att-r2-caps-recipient");
            BrokerMessage Message(Node node, BrokerSession sender, string name) =>
                SendAttached(node, sender, recipient, Source(scratch, "r2-caps", name, "Synthetic capped content."u8.ToArray()));
            var a1 = Message(a, senderA, "a1.txt");
            var a2 = Message(a, senderA, "a2.txt");
            var a3 = Message(a, senderA, "a3.txt");
            var c1 = Message(c, senderC, "c1.txt");
            var c2 = Message(c, senderC, "c2.txt");
            var running = new List<Task<string>>();
            try
            {
                running.Add(Submit(a, b, a1));
                running.Add(Submit(a, b, a2));
                await Until(() => fromA.For(a1.Id) >= 1 && fromA.For(a2.Id) >= 1, "ATTACHMENT_PULL_CAP_PER_PEER: the broker's two pulls are running",
                    () => "requests " + fromA.For(a1.Id) + "/" + fromA.For(a2.Id));
                string perPeer = await Submit(a, b, a3);
                check(perPeer == AttachmentCodes.FetchPending && fromA.For(a3.Id) == 0,
                    "ATTACHMENT_PULL_CAP_PER_PEER: a broker's third concurrent pull waits (attachment_fetch_pending) without a file request");
                running.Add(Submit(c, b, c1));
                await Until(() => fromC.For(c1.Id) >= 1, "ATTACHMENT_PULL_CAP_TOTAL: the third pull is running", () => "requests " + fromC.For(c1.Id));
                string total = await Submit(c, b, c2);
                check(total == AttachmentCodes.FetchPending && fromC.For(c2.Id) == 0,
                    "ATTACHMENT_PULL_CAP_TOTAL: with the pool's pulls all running, another broker's next pull waits without a file request");
            }
            finally
            {
                gate.TrySetResult();
                await Task.WhenAll(running);
            }
        }
    }

    // T-12: an envelope that claims the kind but breaks a rule: refused as invalid for an allowed sender, by policy (first)
    // for one that is not allowed.
    private static async Task InvalidEnvelopeAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var clock = new TestClock();
        var (a, b) = await PairedAsync(scratch, "invalid", clock, identity);
        await using (a)
        await using (b)
        {
            var requests = new Requests(a);
            var sender = a.Register("att-r2-invalid-sender");
            var recipient = b.Register("att-r2-invalid-recipient");
            string entries = string.Join(",", Enumerable.Range(0, 5).Select(index =>
                "{\"index\":" + index + ",\"name\":\"file" + index + ".txt\",\"bytes\":1,\"sha256\":\"" + new string('a', 64) + "\"}"));
            string text = "{\"kind\":\"" + AttachmentEnvelope.Kind + "\",\"text\":\"Synthetic five files.\",\"attachments\":[" + entries + "]}";
            var now = clock.GetUtcNow();
            BrokerMessage Message() => new(Guid.NewGuid(), Guid.NewGuid(), null, sender.Address, recipient.Address, text, now, now.AddMinutes(10));
            var allowed = Message();
            string invalid = await Submit(a, b, allowed);
            b.Attachments!.Policy.Remove(a.Store.BrokerId);
            var unallowed = Message();
            string policy = await Submit(a, b, unallowed);
            check(AttachmentEnvelope.TryParse(text, out _) == AttachmentParse.Invalid && invalid == AttachmentCodes.EnvelopeInvalid &&
                policy == AttachmentCodes.RefusedByPolicy && requests.For(allowed.Id) == 0 && requests.For(unallowed.Id) == 0,
                "ATTACHMENT_INVALID_ENVELOPE_REFUSED: an allowed sender's five-entry envelope is refused attachment_envelope_invalid; an unallowed sender's is refused by policy first");
        }
    }

    // T-11: counts body bytes on the recipient's own HttpClient, independently of PeerFederation's counter.
    internal sealed class CountingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        internal long BodyBytes;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = await base.SendAsync(request, token);
            response.Content = new CountingContent(response.Content, this);
            return response;
        }

        private sealed class CountingContent : HttpContent
        {
            private readonly HttpContent original;
            private readonly CountingHandler owner;
            internal CountingContent(HttpContent original, CountingHandler owner)
            {
                this.original = original;
                this.owner = owner;
                foreach (var header in original.Headers)
                    if (!header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            protected override async Task<Stream> CreateContentReadStreamAsync() => new CountingStream(await original.ReadAsStreamAsync(), owner);
            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            {
                await using var counted = new CountingStream(await original.ReadAsStreamAsync(), owner);
                await counted.CopyToAsync(stream);
            }
            protected override bool TryComputeLength(out long length)
            {
                length = original.Headers.ContentLength ?? 0;
                return original.Headers.ContentLength is not null;
            }
            protected override void Dispose(bool disposing) { if (disposing) original.Dispose(); base.Dispose(disposing); }
        }

        private sealed class CountingStream(Stream inner, CountingHandler owner) : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => Count(await inner.ReadAsync(buffer, token));
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
            private int Count(int read) { Interlocked.Add(ref owner.BodyBytes, read); return read; }
        }
    }
}
