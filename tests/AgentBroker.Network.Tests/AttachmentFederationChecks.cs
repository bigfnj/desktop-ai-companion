using System.Net;
using System.Security.Cryptography;
using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Federation;
using AgentBroker.Worker.Security;

// Lane F (1.10.5): file attachments between real paired nodes over mutual TLS on loopback. Each label names the guard a
// mutation must break; the hygiene recipes attachment-* target these labels.
internal static partial class AttachmentFederationChecks
{
    private const string Note = "Synthetic attachment note.";

    internal static async Task RunAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        Console.WriteLine("WITNESS: POSTPUSH_ATTACHMENTS_EXECUTED");
        Console.WriteLine("ATTACHMENTS_WORKER_SHA256=" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(PeerFederation).Assembly.Location))));
        await PullAsync(scratch, identity, check);
        await MismatchAsync(scratch, identity, check);
        await PolicyAsync(scratch, identity, check);
        await EndpointAsync(scratch, identity, check);
        await OversizeAsync(scratch, identity, check);
        await DeferThenSucceedAsync(scratch, identity, check);
        await SlowPullAsync(scratch, identity, check);
        await HeadOfLineAsync(scratch, identity, check);
        await UnavailableAsync(scratch, identity, check);
        await RoundTwoAsync(scratch, identity, check);
    }

    private static async Task Pair(Node a, Node b)
    {
        await a.Federation.RequestPairAsync(b.Advertisement, default);
        await b.Federation.ApprovePairAsync(b.Federation.PairingOffers.Single().RequestId, default);
        await a.Federation.ApprovePairAsync(a.Federation.PairingOffers.Single().RequestId, default);
    }

    private static PeerTrust TrustOf(Node holder, Node peer) => holder.Store.Peers().Single(p => p.BrokerId == peer.Store.BrokerId);

    private static void Allow(Node recipient, Node sender) => recipient.Attachments!.Policy.Set(TrustOf(recipient, sender), true);

    private static string Source(string scratch, string folder, string name, byte[] bytes)
    {
        string directory = Path.Combine(scratch, "sources-" + folder);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static BrokerMessage SendAttached(Node a, BrokerSession sender, BrokerSession recipient, params string[] files)
    {
        Guid id = Guid.NewGuid();
        string envelope = a.Attachments!.Stage(id, Note, files, out _);
        return a.Store.Send(sender.Address, recipient.Address, id, envelope, null, 60, a.Store.BrokerId).Message;
    }

    // The label names the state waited for and the observer says what was seen instead. The first line is the label and the
    // time bound only, so a hygiene recipe can pin it exactly; the observed state (timing-dependent, e.g. Accepted versus
    // Submitting) follows on its own line.
    private static async Task Until(Func<bool> condition, string label, Func<string> observe, int seconds = 40)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        try { while (!condition()) await Task.Delay(25, deadline.Token); }
        catch (OperationCanceledException error)
        {
            throw new InvalidOperationException(label + " (not reached within " + seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " s)" + Environment.NewLine + "  observed: " + observe(), error);
        }
    }

    private static string Seen(Node node, Guid id) =>
        node.Store.FindReceipt(id) is { } receipt ? receipt.Status + "/" + (receipt.Reason ?? "null") : "no receipt";

    // N-1: two files arrive verified, carry ZoneId=3, and the sender ends Submitted. N-11: a retry of the admitted message
    // never pulls again.
    private static async Task PullAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var clock = new TestClock();
        await using var a = await Node.Create(Path.Combine(scratch, "att-pull-a"), clock, identity, attachments: true);
        await using var b = await Node.Create(Path.Combine(scratch, "att-pull-b"), clock, identity, attachments: true);
        await Pair(a, b);
        Allow(b, a);
        var sender = a.Register("att-pull-sender");
        var recipient = b.Register("att-pull-recipient");
        byte[] large = RandomNumberGenerator.GetBytes(1024 * 1024 + 17);
        byte[] small = "Synthetic attachment content.\r\n"u8.ToArray();
        var message = SendAttached(a, sender, recipient, Source(scratch, "pull", "large.bin", large), Source(scratch, "pull", "notes.txt", small));
        await using (var runtime = new BrokerRuntime(a.Store, a.Federation, []))
        {
            runtime.Start(default);
            await Until(() => a.Store.FindReceipt(message.Id)?.Status == MessageStatus.Submitted,
                "ATTACHMENT_PULL_VERIFIED_MOTW: the sender's message reaches Submitted once the recipient has verified both files",
                () => "sender " + Seen(a, message.Id) + "; recipient " + Seen(b, message.Id) + "; file requests served " + a.Pairing.AttachmentRequests);
        }
        string folder = b.Attachments!.InboxFolder(a.Store.BrokerId, message.Id);
        string largePath = Path.Combine(folder, "0-large.bin"), smallPath = Path.Combine(folder, "1-notes.txt");
        check(b.Store.FindReceipt(message.Id)?.Message == message && File.Exists(largePath) && File.Exists(smallPath) &&
            File.ReadAllBytes(largePath).AsSpan().SequenceEqual(large) && File.ReadAllBytes(smallPath).AsSpan().SequenceEqual(small),
            "ATTACHMENT_PULL_VERIFIED_MOTW: the recipient admits the message once and holds both files byte for byte");
        check(File.Exists(largePath + ":Zone.Identifier") && File.ReadAllText(largePath + ":Zone.Identifier").Contains("ZoneId=3") &&
            File.ReadAllText(smallPath + ":Zone.Identifier").Contains("ZoneId=3"),
            "ATTACHMENT_PULL_VERIFIED_MOTW: every received file carries a ZoneId=3 Mark of the Web");
        check(File.Exists(b.Attachments.ReadmePath), "ATTACHMENT_README: an enabled store writes the README the pane reveals");
        int served = a.Pairing.AttachmentRequests;
        int pulls = Volatile.Read(ref b.Federation.AttachmentPullsStarted);
        var again = await a.Federation.SubmitAsync(TrustOf(a, b), message, default);
        // T-10: no file request AND no pull started (a pull answered from verified files would make no request either).
        check(again.Message == message && a.Pairing.AttachmentRequests == served && Volatile.Read(ref b.Federation.AttachmentPullsStarted) == pulls,
            "ATTACHMENT_IDEMPOTENT_RETRY: a retry of an admitted message is answered by the store and never pulls again");
        b.Federation.RevokePeer(a.Store.BrokerId);
        check(!File.ReadAllText(Path.Combine(b.Directory, "attachment-peers.json")).Contains(a.Store.BrokerId.ToString("D"), StringComparison.OrdinalIgnoreCase),
            "ATTACHMENT_REVOKE_CLEARS_ALLOW: revoking a broker removes its attachment permission");
    }

    // N-2: the sender's staged file changes after the envelope was composed; the recipient refuses and keeps nothing.
    private static async Task MismatchAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var clock = new TestClock();
        await using var a = await Node.Create(Path.Combine(scratch, "att-mismatch-a"), clock, identity, attachments: true);
        await using var b = await Node.Create(Path.Combine(scratch, "att-mismatch-b"), clock, identity, attachments: true);
        await Pair(a, b);
        Allow(b, a);
        var sender = a.Register("att-mismatch-sender");
        var recipient = b.Register("att-mismatch-recipient");
        var message = SendAttached(a, sender, recipient, Source(scratch, "mismatch", "data.bin", RandomNumberGenerator.GetBytes(4096)));
        File.WriteAllBytes(Path.Combine(a.Attachments!.OutboxFolder(message.Id), "0-data.bin"), RandomNumberGenerator.GetBytes(4096));
        long published = b.Attachments!.PublishedFiles; // T-8: unverified bytes never reach the disk, even briefly
        await using (var runtime = new BrokerRuntime(a.Store, a.Federation, []))
        {
            runtime.Start(default);
            await Until(() => a.Store.FindReceipt(message.Id)?.Status == MessageStatus.Refused,
                "ATTACHMENT_MISMATCH_REFUSED: the sender records the message whose staged file was altered as Refused",
                () => "sender " + Seen(a, message.Id) + " (expected Refused/attachment_mismatch); recipient " + Seen(b, message.Id));
        }
        check(a.Store.FindReceipt(message.Id)?.Reason == AttachmentCodes.Mismatch && b.Store.FindReceipt(message.Id) is null &&
            !Directory.Exists(b.Attachments.InboxFolder(a.Store.BrokerId, message.Id)) && b.Attachments.PublishedFiles == published,
            "ATTACHMENT_MISMATCH_REFUSED: a file that does not hash to its declared value is refused and nothing is kept");
    }

    // N-3: attachments off at the recipient, then on but this sender not allowed: refused with no request to the sender.
    private static async Task PolicyAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        foreach (bool enabled in new[] { false, true })
        {
            var clock = new TestClock();
            string tag = enabled ? "on" : "off";
            await using var a = await Node.Create(Path.Combine(scratch, "att-policy-a-" + tag), clock, identity, attachments: true);
            await using var b = await Node.Create(Path.Combine(scratch, "att-policy-b-" + tag), clock, identity, attachments: enabled);
            await Pair(a, b);
            var sender = a.Register("att-policy-sender");
            var recipient = b.Register("att-policy-recipient");
            var message = SendAttached(a, sender, recipient, Source(scratch, "policy-" + tag, "data.txt", "Synthetic."u8.ToArray()));
            await using (var runtime = new BrokerRuntime(a.Store, a.Federation, []))
            {
                runtime.Start(default);
                await Until(() => a.Store.FindReceipt(message.Id)?.Status == MessageStatus.Refused,
                    "ATTACHMENT_POLICY_REFUSED (attachments " + tag + " at the recipient, sender not allowed): the sender records the message as Refused by the recipient broker's attachment policy",
                    () => "sender " + Seen(a, message.Id) + " (expected Refused/attachments_refused_by_policy); recipient " + Seen(b, message.Id) +
                        "; file requests served by the sender " + a.Pairing.AttachmentRequests);
            }
            check(a.Store.FindReceipt(message.Id)?.Reason == AttachmentCodes.RefusedByPolicy && a.Pairing.AttachmentRequests == 0 &&
                b.Store.FindReceipt(message.Id) is null,
                "ATTACHMENT_POLICY_REFUSED: attachments " + tag + " without an allow for this sender refuse before any file request");
        }
    }

    // N-4 and N-5: only the message's own recipient broker can fetch a file; another paired broker gets 404 and an unpaired
    // TLS client 403.
    private static async Task EndpointAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var clock = new TestClock();
        await using var a = await Node.Create(Path.Combine(scratch, "att-endpoint-a"), clock, identity, attachments: true);
        await using var b = await Node.Create(Path.Combine(scratch, "att-endpoint-b"), clock, identity, attachments: true);
        await using var c = await Node.Create(Path.Combine(scratch, "att-endpoint-c"), clock, identity, attachments: true);
        await using var d = await Node.Create(Path.Combine(scratch, "att-endpoint-d"), clock, identity, attachments: true);
        await Pair(a, b);
        await Pair(a, c);
        var sender = a.Register("att-endpoint-sender");
        var recipient = b.Register("att-endpoint-recipient");
        byte[] content = RandomNumberGenerator.GetBytes(2048);
        var message = SendAttached(a, sender, recipient, Source(scratch, "endpoint", "data.bin", content));
        string route = "federation/attachments/" + message.Id.ToString("D") + "/0";
        using (var asRecipient = a.Client(b.Identity))
        using (var answer = await asRecipient.GetAsync(route))
            check(answer.StatusCode == HttpStatusCode.OK && (await answer.Content.ReadAsByteArrayAsync()).AsSpan().SequenceEqual(content),
                "ATTACHMENT_ENDPOINT_RECIPIENT_ONLY: the recipient's own broker receives the staged bytes (control)");
        using (var asThird = a.Client(c.Identity))
        using (var answer = await asThird.GetAsync(route))
            check(answer.StatusCode == HttpStatusCode.NotFound,
                "ATTACHMENT_ENDPOINT_RECIPIENT_ONLY: another paired broker cannot fetch a file addressed to someone else");
        using (var unpaired = a.Client(d.Identity))
        using (var answer = await unpaired.GetAsync(route))
            check(answer.StatusCode == HttpStatusCode.Forbidden, "ATTACHMENT_UNPAIRED_403: an unpaired TLS client is refused");
    }

    // N-6: the sender announces more bytes than declared; the recipient refuses before reading any body byte.
    private static async Task OversizeAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var clock = new TestClock();
        await using var a = await Node.Create(Path.Combine(scratch, "att-oversize-a"), clock, identity, attachments: true);
        await using var b = await Node.Create(Path.Combine(scratch, "att-oversize-b"), clock, identity, attachments: true);
        await Pair(a, b);
        Allow(b, a);
        var sender = a.Register("att-oversize-sender");
        var recipient = b.Register("att-oversize-recipient");
        const int declared = 4096;
        var message = SendAttached(a, sender, recipient, Source(scratch, "oversize", "data.bin", RandomNumberGenerator.GetBytes(declared)));
        a.Pairing.BeforeAttachment = async context =>
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentLength = declared + 1;
            await context.Response.Body.WriteAsync(new byte[declared + 1]);
            return true;
        };
        // T-11: body bytes are counted on the recipient's own HttpClient (a DelegatingHandler), not by the product's counter.
        CountingHandler? counter = null;
        b.Federation.AttachmentHandler = inner => counter = new CountingHandler(inner);
        long published = b.Attachments!.PublishedFiles;
        await using (var runtime = new BrokerRuntime(a.Store, a.Federation, []))
        {
            runtime.Start(default);
            await Until(() => a.Store.FindReceipt(message.Id)?.Status == MessageStatus.Refused,
                "ATTACHMENT_OVERSIZE_REFUSED_BEFORE_DOWNLOAD: the sender records the message whose file is served oversized as Refused",
                () => "sender " + Seen(a, message.Id) + " (expected Refused/attachment_mismatch); recipient " + Seen(b, message.Id) +
                    "; body bytes read " + (counter is null ? "no request" : Interlocked.Read(ref counter.BodyBytes).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        check(a.Store.FindReceipt(message.Id)?.Reason == AttachmentCodes.Mismatch && b.Store.FindReceipt(message.Id) is null &&
            counter is not null && Interlocked.Read(ref counter.BodyBytes) == 0 && b.Attachments.PublishedFiles == published,
            "ATTACHMENT_OVERSIZE_REFUSED_BEFORE_DOWNLOAD: a Content-Length other than the declared size is refused with no body byte read");
    }

    // N-7: one failed file request defers the message (Accepted, attachment_fetch_pending); the next forward succeeds.
    private static async Task DeferThenSucceedAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var clock = new TestClock();
        await using var a = await Node.Create(Path.Combine(scratch, "att-defer-a"), clock, identity, attachments: true);
        await using var b = await Node.Create(Path.Combine(scratch, "att-defer-b"), clock, identity, attachments: true);
        await Pair(a, b);
        Allow(b, a);
        var sender = a.Register("att-defer-sender");
        var recipient = b.Register("att-defer-recipient");
        var message = SendAttached(a, sender, recipient, Source(scratch, "defer", "data.txt", "Synthetic deferred content."u8.ToArray()));
        int requests = 0;
        a.Pairing.BeforeAttachment = context =>
        {
            if (Interlocked.Increment(ref requests) != 1) return Task.FromResult(false);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return Task.FromResult(true);
        };
        bool sawDeferral = false;
        await using (var runtime = new BrokerRuntime(a.Store, a.Federation, []))
        {
            runtime.Start(default);
            await Until(() =>
            {
                var receipt = a.Store.FindReceipt(message.Id);
                sawDeferral |= receipt is { Status: MessageStatus.Accepted, Reason: AttachmentCodes.FetchPending, Attempts: 0 };
                return receipt?.Status == MessageStatus.Submitted;
            }, "ATTACHMENT_FETCH_FAILED_DEFERS_THEN_SUCCEEDS: the deferred message reaches Submitted on a later forward",
                () => "sender " + Seen(a, message.Id) + "; deferral seen " + sawDeferral + "; file requests " + requests);
        }
        check(sawDeferral && b.Store.Messages(recipient.Address).Count(row => row.Message.Id == message.Id) == 1 && requests >= 2,
            "ATTACHMENT_FETCH_FAILED_DEFERS_THEN_SUCCEEDS: a failed file request defers the same message, and a later forward admits it once");
    }

    // N-8 (D-1): a pull slower than the inline wait defers instead of letting the sender's forward time out into Uncertain.
    private static async Task SlowPullAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var clock = new TestClock();
        await using var a = await Node.Create(Path.Combine(scratch, "att-slow-a"), clock, identity, attachments: true);
        await using var b = await Node.Create(Path.Combine(scratch, "att-slow-b"), clock, identity, attachments: true);
        await Pair(a, b);
        Allow(b, a);
        var sender = a.Register("att-slow-sender");
        var recipient = b.Register("att-slow-recipient");
        var message = SendAttached(a, sender, recipient, Source(scratch, "slow", "data.txt", "Synthetic slow content."u8.ToArray()));
        // Longer than the sender's 10 s forward, so only the bounded inline wait keeps the forward from timing out.
        a.Pairing.BeforeAttachment = async _ => { await Task.Delay(TimeSpan.FromSeconds(11)); return false; };
        var seen = new HashSet<MessageStatus>();
        await using (var runtime = new BrokerRuntime(a.Store, a.Federation, []))
        {
            runtime.Start(default);
            await Until(() =>
            {
                var status = a.Store.FindReceipt(message.Id)?.Status;
                if (status is { } value) seen.Add(value);
                return status is MessageStatus.Submitted or MessageStatus.Uncertain or MessageStatus.Refused;
            }, "ATTACHMENT_SLOW_PULL_NO_UNCERTAIN: the slow pull's message settles as Submitted, Uncertain or Refused",
                () => "sender " + Seen(a, message.Id) + "; statuses seen " + string.Join(",", seen));
        }
        check(!seen.Contains(MessageStatus.Uncertain) && a.Store.FindReceipt(message.Id)?.Status == MessageStatus.Submitted &&
            b.Store.FindReceipt(message.Id) is not null,
            "ATTACHMENT_SLOW_PULL_NO_UNCERTAIN: a pull longer than the inline wait defers and later admits, never Uncertain");
    }

    // N-9 (D-3): a pull that keeps failing is refused once the deferral bound passes, so the pair's next message goes.
    private static async Task HeadOfLineAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var clock = new TestClock();
        await using var a = await Node.Create(Path.Combine(scratch, "att-hol-a"), clock, identity, attachments: true);
        await using var b = await Node.Create(Path.Combine(scratch, "att-hol-b"), clock, identity, attachments: true);
        await Pair(a, b);
        Allow(b, a);
        var sender = a.Register("att-hol-sender");
        var recipient = b.Register("att-hol-recipient");
        var attached = SendAttached(a, sender, recipient, Source(scratch, "hol", "data.txt", "Synthetic blocked content."u8.ToArray()));
        var text = a.Store.Send(sender.Address, recipient.Address, Guid.NewGuid(), "Synthetic message queued behind an attachment.", null, 60,
            a.Store.BrokerId).Message;
        a.Pairing.BeforeAttachment = context => { context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable; return Task.FromResult(true); };
        await using (var runtime = new BrokerRuntime(a.Store, a.Federation, []))
        {
            runtime.Start(default);
            await Until(() => a.Store.FindReceipt(attached.Id) is { Status: MessageStatus.Accepted, Reason: AttachmentCodes.FetchPending },
                "ATTACHMENT_HEAD_OF_LINE_BOUND: the attachment message is deferred as Accepted with attachment_fetch_pending",
                () => "attachment message " + Seen(a, attached.Id));
            check(b.Store.FindReceipt(text.Id) is null, "ATTACHMENT_HEAD_OF_LINE_BOUND: the pair's later message waits behind the deferred one");
            clock.Advance(PeerFederation.AttachmentDeferralBound + TimeSpan.FromMinutes(1));
            b.Store.Register(recipient with { LeaseUntil = clock.GetUtcNow().AddMinutes(1) });
            await Until(() => b.Store.FindReceipt(text.Id) is not null,
                "ATTACHMENT_HEAD_OF_LINE_BOUND: past the deferral bound the text message queued behind it reaches the recipient",
                () => "text message at the recipient " + Seen(b, text.Id) + "; attachment message " + Seen(a, attached.Id) +
                    " (expected Refused/attachment_fetch_failed)");
        }
        check(a.Store.FindReceipt(attached.Id) is { Status: MessageStatus.Refused, Reason: AttachmentCodes.FetchFailed } &&
            b.Store.FindReceipt(attached.Id) is null,
            "ATTACHMENT_HEAD_OF_LINE_BOUND: past the deferral bound the attachment message is refused and the next one is delivered");
    }

    // N-10 (D-4): envelope text whose sender staged nothing (an older broker, or forged text) is refused, not deferred.
    private static async Task UnavailableAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var clock = new TestClock();
        await using var a = await Node.Create(Path.Combine(scratch, "att-unavailable-a"), clock, identity, attachments: true);
        await using var b = await Node.Create(Path.Combine(scratch, "att-unavailable-b"), clock, identity, attachments: true);
        await Pair(a, b);
        Allow(b, a);
        var sender = a.Register("att-unavailable-sender");
        var recipient = b.Register("att-unavailable-recipient");
        string forged = AttachmentEnvelope.Compose(Note, [new AttachmentEntry(0, "data.txt", 10, new string('a', 64))]);
        var message = a.Store.Send(sender.Address, recipient.Address, Guid.NewGuid(), forged, null, 60, a.Store.BrokerId).Message;
        bool deferred = false;
        await using (var runtime = new BrokerRuntime(a.Store, a.Federation, []))
        {
            runtime.Start(default);
            await Until(() =>
            {
                var receipt = a.Store.FindReceipt(message.Id);
                deferred |= receipt?.Reason == AttachmentCodes.FetchPending;
                return receipt?.Status == MessageStatus.Refused;
            }, "ATTACHMENT_UNAVAILABLE_REFUSED: the sender records the message whose sender staged no file as Refused",
                () => "sender " + Seen(a, message.Id) + " (expected Refused/attachment_unavailable); deferred at least once " + deferred);
        }
        // Refused, never deferred: a sender with nothing staged is a definite answer (DIS-1).
        check(a.Store.FindReceipt(message.Id)?.Reason == AttachmentCodes.Unavailable && b.Store.FindReceipt(message.Id) is null && !deferred,
            "ATTACHMENT_UNAVAILABLE_REFUSED: a sender with no staged file is refused attachment_unavailable without a deferral");
    }
}
