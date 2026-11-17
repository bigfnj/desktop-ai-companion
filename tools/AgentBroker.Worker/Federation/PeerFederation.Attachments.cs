using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using AgentBroker.Core;
using AgentBroker.Worker.Attachments;

namespace AgentBroker.Worker.Federation;

// File attachments between paired brokers (lane F, 1.10.5; design: marathon-1105 ITEM-DESIGNS "Lane F" as amended by the
// lead's decisions D-1..D-9 and the round-2 request 003-F-round2.md). Pull, not push: a message carries an envelope naming
// each file's size and SHA-256, and the RECIPIENT broker fetches every file from the sender broker over the same pinned
// mutual-TLS channel, verifies it in memory, and publishes it into quarantine with a Mark of the Web before the message is
// admitted. A message is never admitted with partial, unverified or missing content.
public sealed partial class PeerFederation
{
    private readonly AttachmentStore? attachmentStore;

    // Null: no attachment store was wired (a test or embedding), so ordinary text is admitted exactly as before and every
    // envelope is refused by policy.
    public AttachmentStore? Attachments
    {
        get => attachmentStore;
        init => attachmentStore = value;
    }

    // D-1: the message route waits this long for a pull, well inside the sender's 10 s forward, then answers a deferral and
    // lets the pull run on; the sender's next forward admits from the verified files.
    internal static readonly TimeSpan AttachmentInlineWait = TimeSpan.FromSeconds(5);
    // D-3: a sender pair's later messages wait behind a deferred one (BrokerStore.BeginDelivery), so deferral is bounded.
    internal static readonly TimeSpan AttachmentDeferralBound = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan AttachmentFileTimeout = TimeSpan.FromSeconds(30);
    // F-8: a finished pull's state waits this long for the sender's next forward (normally 5 s away), then goes.
    internal static readonly TimeSpan CompletedPullGrace = TimeSpan.FromMinutes(2);
    private enum PullOutcome { Verified, Mismatch, Unavailable, Transient, Revoked, QuarantineUnavailable }
    private sealed class PullState(Guid peer, string textSha256, CancellationTokenSource cancel)
    {
        internal Guid Peer { get; } = peer;
        internal string TextSha256 { get; } = textSha256;
        internal CancellationTokenSource Cancel { get; } = cancel;
        internal Task<PullOutcome> Task { get; set; } = System.Threading.Tasks.Task.FromResult(PullOutcome.Transient);
        internal long? CompletedAt { get; set; }
    }
    private readonly object attachmentGate = new();
    private readonly Dictionary<(Guid Peer, Guid Message), PullState> pulls = new();
    // F-7/F-21: when the first ACTUAL pull for a message started, on the monotonic clock; capacity waits start nothing.
    private readonly Dictionary<(Guid Peer, Guid Message), long> firstPull = new();
    private readonly Dictionary<Guid, int> serving = new();
    private readonly CancellationTokenSource attachmentStop = new();
    // Test evidence only: body bytes this broker has read from paired brokers' attachment responses, and pull starts.
    internal long AttachmentBodyBytesRead;
    internal int AttachmentPullsStarted;
    internal int AttachmentPullStateCount { get { lock (attachmentGate) return pulls.Count; } }
    // F-8 evidence: linked cancellation sources created for pulls and not yet disposed.
    internal int AttachmentLinkedSourcesLive;
    // Test seam (round 2b), null in production: runs inside the admission gate after the F-1 recheck and before the allow
    // check and store.Accept, so a check can put a sweep or an allow removal exactly there.
    internal Action? BeforeAdmission { get; set; }
    // Test seam (round 6, DECISIONS 119), null in production: runs once right before GetOrStartPull, after RequireOneText, so a
    // check can hold one forward there while a forward of the same message ID with another text passes RequireOneText too
    // (S-019, item 17). Nothing else can pause a forward in that window.
    internal Action? BeforePullLookup { get; set; }
    // Test seam (T-11), null in production: wraps the handler of the client that fetches files, so a check can count body
    // bytes independently of this class's own counter.
    internal Func<HttpMessageHandler, HttpMessageHandler>? AttachmentHandler { get; set; }

    private void MapAttachmentEndpoints(WebApplication application) =>
        application.MapGet("/federation/attachments/{id:guid}/{index:int}", (HttpContext c, Guid id, int index) => Handle(c, async () =>
        {
            Guid caller = RequirePeer(c);
            var attachments = Attachments;
            var receipt = store.FindReceiptWithText(id);
            // Only this broker's own outstanding message, only to the broker it is addressed to, only while attachments are on.
            if (attachments is not { Enabled: true } || receipt is null || receipt.Message.Sender.BrokerId != store.BrokerId ||
                receipt.Message.Recipient.BrokerId != caller ||
                receipt.Status is not (MessageStatus.Accepted or MessageStatus.Submitting) || receipt.Message.ExpiresAt <= Now ||
                AttachmentEnvelope.TryParse(receipt.Message.Text, out var envelope) != AttachmentParse.Valid ||
                index < 0 || index >= envelope!.Attachments.Count) return Results.NotFound();
            if (!TryEnterServing(caller, attachments.Limits.MaximumServesPerPeer)) return Results.StatusCode(429);
            try
            {
                var entry = envelope.Attachments[index];
                switch (attachments.OpenOutgoing(id, receipt.Message.Text, entry, out var handle))
                {
                    // F-16: a staged file that exists but cannot be opened now is a transient condition, never "unavailable".
                    case OutgoingState.Busy: return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                    case OutgoingState.Missing: return Results.NotFound();
                }
                using (handle)
                {
                    // Recheck after the open, as the message route does after its body read: a revocation in between serves nothing.
                    if (RequirePeer(c) != caller) throw new UnauthorizedAccessException();
                    c.Response.StatusCode = StatusCodes.Status200OK;
                    c.Response.ContentType = "application/octet-stream";
                    c.Response.ContentLength = entry.Bytes;
                    await using var file = new FileStream(handle!, FileAccess.Read, 81920, false);
                    await file.CopyToAsync(c.Response.Body, c.RequestAborted);
                    return Results.Empty;
                }
            }
            finally { ExitServing(caller); }
        }));

    private bool TryEnterServing(Guid caller, int maximum)
    {
        lock (attachmentGate)
        {
            int count = serving.GetValueOrDefault(caller);
            if (count >= maximum) return false;
            serving[caller] = count + 1;
            return true;
        }
    }

    private void ExitServing(Guid caller)
    {
        lock (attachmentGate)
        {
            int count = serving.GetValueOrDefault(caller) - 1;
            if (count <= 0) serving.Remove(caller); else serving[caller] = count;
        }
    }

    // The /federation/messages admission, after the body read and the paired-identity recheck. Ordinary text goes straight
    // to the store; an envelope is admitted only once every file is verified in quarantine AND still there.
    private async Task<IResult> AcceptMessageAsync(HttpContext c, Guid caller, BrokerMessage message)
    {
        // R0: a retry of an admitted message never pulls again; the store's idempotent path answers it.
        if (store.FindReceipt(message.Id) is not null) return Results.Json(store.Accept(message, caller), Json);
        var parse = AttachmentEnvelope.TryParse(message.Text, out var envelope);
        var attachments = Attachments;
        string textSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(message.Text)));
        var key = (caller, message.Id);
        // F-15 and round 2b (M2, M3): one message ID, one text. While a pull for this ID runs, or a received folder holds files
        // for it, any other text under the ID from the same broker, another envelope or plain text, is refused.
        if (attachments is not null) RequireOneText(key, textSha256, attachments);
        if (parse == AttachmentParse.NotEnvelope) return Results.Json(store.Accept(message, caller), Json);
        var peer = store.Peers().SingleOrDefault(candidate => candidate.BrokerId == caller);
        // R3, before anything else: a broker the owner has not allowed learns only that.
        if (attachments is not { Enabled: true } || !attachments.Policy.Allows(peer))
            throw new BrokerException(AttachmentCodes.RefusedByPolicy, "This broker does not accept attachments from that broker.");
        if (parse == AttachmentParse.Invalid)
            throw new BrokerException(AttachmentCodes.EnvelopeInvalid, "The attachment envelope is invalid.");
        // F-19: what the store will check anyway, before a single byte is pulled; the same codes the store would answer. Round 2b:
        // only before the FIRST pull for this message. Once one ran, the final store.Accept decides, so a lease flicker during
        // the deferral's re-forwards cannot refuse a message whose recipient is back by the time its files are ready.
        if (!PullStarted(key)) RequireAdmissible(caller, message);
        BeforePullLookup?.Invoke();
        var state = GetOrStartPull(peer!, message, envelope!, textSha256, attachments);
        if (state is null) return Defer(key, message, caller);
        try { await state.Task.WaitAsync(AttachmentInlineWait, TimeProvider.System, c.RequestAborted); }
        catch (TimeoutException) { return Defer(key, message, caller); }
        switch (state.Task.Result)
        {
            case PullOutcome.Verified:
                // F-1: a finished pull is a claim about files on disk; check them again now. A sweep (or a hand) may have
                // removed them since, and a message must never be admitted with files missing. The next forward pulls afresh.
                // Round 2b: the recheck, the allow and the admission run under the store's admission gate, which the sweep
                // takes for each received folder too, so nothing can delete the files between the recheck and the admission;
                // and an allow the owner withdrew during the pull refuses the message here.
                MessageReceipt? admitted = null;
                bool present, allowed;
                lock (attachments.AdmissionGate)
                {
                    present = envelope!.Attachments.All(entry => attachments.HasVerified(caller, message.Id, entry));
                    BeforeAdmission?.Invoke();
                    // Round 6 (S-018, item 10): another forward of this message may have admitted it while this one waited for
                    // the gate. The R0 test again, before the allow check: the store's idempotent path answers this forward
                    // too, so an allow withdrawn since that admission can neither refuse it nor delete its files.
                    bool known = store.FindReceipt(message.Id) is not null;
                    allowed = known || attachments.Policy.Allows(store.Peers().SingleOrDefault(candidate => candidate.BrokerId == caller));
                    if (known || present && allowed)
                    {
                        try { admitted = store.Accept(message, caller); }
                        catch (BrokerException)
                        {
                            Abandon(key, attachments);
                            throw;
                        }
                    }
                }
                if (admitted is not null)
                {
                    Finish(key, state, attachments);
                    return Results.Json(admitted, Json);
                }
                if (!allowed)
                {
                    Abandon(key, attachments);
                    throw new BrokerException(AttachmentCodes.RefusedByPolicy, "This broker no longer accepts attachments from that broker.");
                }
                Drop(key, state, attachments);
                return Defer(key, message, caller);
            case PullOutcome.Mismatch:
                Abandon(key, attachments);
                throw new BrokerException(AttachmentCodes.Mismatch, "An attachment did not match its declared size or SHA-256.");
            case PullOutcome.Unavailable:
                Abandon(key, attachments);
                throw new BrokerException(AttachmentCodes.Unavailable, "The sender has no such attachment.");
            case PullOutcome.Revoked:
                // F-22 and round 2b: the pairing ended, or the owner withdrew the allow, during the pull; nothing was published
                // after that and nothing was admitted, so the sender ends Refused by policy.
                Abandon(key, attachments);
                throw new BrokerException(AttachmentCodes.RefusedByPolicy, "This broker no longer accepts attachments from that broker.");
            case PullOutcome.QuarantineUnavailable:
                // F-6: the quarantine cannot hold files at all; a definite answer, shown on this broker's status too.
                Abandon(key, attachments);
                attachments.MarkQuarantineUnavailable();
                throw new BrokerException(AttachmentCodes.QuarantineUnavailable, "This broker cannot store received attachments.");
            default:
                // Transient: the next forward starts a fresh pull (verified files are kept), within the deferral bound.
                Drop(key, state, attachments);
                return Defer(key, message, caller);
        }
    }

    // F-15 / round 2b: the text this broker already holds for a message ID (a running pull's, or the received folder's manifest).
    private void RequireOneText((Guid Peer, Guid Message) key, string textSha256, AttachmentStore attachments)
    {
        string? held;
        lock (attachmentGate) held = pulls.TryGetValue(key, out var state) ? state.TextSha256 : null;
        held ??= attachments.ReceivedEnvelopeSha256(key.Peer, key.Message);
        if (held is not null && held != textSha256)
            throw new BrokerException(AttachmentCodes.EnvelopeInvalid, "A different text was sent under a message ID whose attachments are pending.");
    }

    private bool PullStarted((Guid Peer, Guid Message) key)
    {
        lock (attachmentGate) return pulls.ContainsKey(key) || firstPull.ContainsKey(key);
    }

    // F-19: the store's own admission checks that a pull would otherwise precede.
    private void RequireAdmissible(Guid caller, BrokerMessage message)
    {
        if (message.Sender.BrokerId != caller) throw new BrokerException("sender_mismatch", "The sender must belong to the authenticated broker.");
        if (message.Recipient.BrokerId != store.BrokerId) throw new BrokerException("transit_refused", "Only direct federation is accepted.");
        if (message.ExpiresAt <= Now) throw new BrokerException("invalid_message", "The message has expired.");
        if (!store.IsAddressable(message.Recipient)) throw new BrokerException("recipient_unavailable", "The recipient is not available.");
        if (store.Directory().SingleOrDefault(row => row.Address == message.Recipient) is { } row)
        {
            if (row.IsPrivate) throw new BrokerException("private_session", "The recipient is private.");
            if (!row.Capabilities.HasFlag(SessionCapabilities.Receive)) throw new BrokerException("recipient_cannot_receive", "The recipient cannot receive.");
        }
    }

    // A deferral; or once the bound has passed since this message's first ACTUAL pull, the final refusal (D-3, F-21). Round 6
    // (S-017, item 5): unless another forward of this message admitted it while this one waited for the admission gate in
    // Abandon; then its folder stays and the store's idempotent path answers this forward too (the R0 test; after a revoke
    // the store answers unpaired_peer instead, and the folder still stays).
    private IResult Defer((Guid Peer, Guid Message) key, BrokerMessage message, Guid caller)
    {
        long? first;
        lock (attachmentGate) first = firstPull.TryGetValue(key, out long started) ? started : null;
        if (first is null || clock.GetElapsedTime(first.Value) < AttachmentDeferralBound)
            throw new BrokerException(AttachmentCodes.FetchPending, "The attachments are still being fetched; retry later.");
        if (Attachments is { } attachments && Abandon(key, attachments)) return Results.Json(store.Accept(message, caller), Json);
        throw new BrokerException(AttachmentCodes.FetchFailed, "The attachments could not be fetched in time.");
    }

    private PullState? GetOrStartPull(PeerTrust peer, BrokerMessage message, AttachmentEnvelopeData envelope, string textSha256,
        AttachmentStore attachments)
    {
        var key = (peer.BrokerId, message.Id);
        var limits = attachments.Limits;
        lock (attachmentGate)
        {
            // F-8 / F-1: stale states go BEFORE any lookup, so a cached outcome can never outlive what it described.
            CleanupPulls(attachments);
            if (pulls.TryGetValue(key, out var existing))
            {
                // Round 6 (S-019, item 17): the text is bound to the pull under this gate. A forward with another text that
                // passed RequireOneText before this pull was registered is refused here while the pull's state is held.
                if (existing.TextSha256 != textSha256)
                    throw new BrokerException(AttachmentCodes.EnvelopeInvalid, "A different text was sent under a message ID whose attachments are pending.");
                return existing;
            }
            int running = pulls.Values.Count(pull => !pull.Task.IsCompleted);
            int runningForPeer = pulls.Values.Count(pull => pull.Peer == peer.BrokerId && !pull.Task.IsCompleted);
            if (pulls.Count >= limits.MaximumPullStates || running >= limits.MaximumPulls || runningForPeer >= limits.MaximumPullsPerPeer)
                return null;
            // Round 2b (M4): a broker's share of the pull-state table, finished states included, so one broker cannot fill it.
            if (pulls.Values.Count(pull => pull.Peer == peer.BrokerId) >= limits.MaximumPullStatesPerPeer) return null;
            // F-7: the deferral table never evicts an entry younger than the bound; when it is full no new pull starts, and the
            // sender's own stored bound ends the wait.
            if (!firstPull.ContainsKey(key) && firstPull.Count >= limits.MaximumDeferrals) return null;
            // Round 2b (M4): and a broker's share of the deferral table.
            if (!firstPull.ContainsKey(key) && firstPull.Keys.Count(entry => entry.Peer == peer.BrokerId) >= limits.MaximumDeferralsPerPeer) return null;
            switch (attachments.Reserve(peer.BrokerId, message.Id, envelope.TotalBytes))
            {
                // F-5 (DECISIONS 39): refused only when ADMITTED files leave no room (in the pool or this broker's share).
                case ReserveResult.RefuseFull:
                    throw new BrokerException(AttachmentCodes.QuarantineFull, "The attachment quarantine is full.");
                case ReserveResult.Defer:
                    return null;
            }
            var cancel = CancellationTokenSource.CreateLinkedTokenSource(attachmentStop.Token);
            Interlocked.Increment(ref AttachmentLinkedSourcesLive);
            var state = new PullState(peer.BrokerId, textSha256, cancel);
            firstPull.TryAdd(key, clock.GetTimestamp());
            pulls[key] = state;
            Interlocked.Increment(ref AttachmentPullsStarted);
            state.Task = Task.Run(async () =>
            {
                var outcome = await PullAsync(peer, message.Id, envelope, message.Text, attachments, cancel.Token);
                lock (attachmentGate) state.CompletedAt = clock.GetTimestamp();
                // The reservation covers a RUNNING pull. Once it ends, what it published counts as unadmitted bytes on disk
                // (AttachmentStore.Reserve), so holding it longer would only stop the sweep from ever removing a verified
                // folder whose forward never came back.
                attachments.Release(peer.BrokerId, message.Id);
                return outcome;
            });
            return state;
        }
    }

    // Under attachmentGate. Finished states past their grace go (with their reservation); deferral entries go once well past
    // the bound, never earlier.
    private void CleanupPulls(AttachmentStore attachments)
    {
        foreach (var (key, state) in pulls.Where(pair => pair.Value.CompletedAt is { } done && clock.GetElapsedTime(done) > CompletedPullGrace).ToArray())
        {
            pulls.Remove(key);
            attachments.Release(key.Peer, key.Message);
            DisposeWhenDone(state);
        }
        foreach (var key in firstPull.Where(pair => clock.GetElapsedTime(pair.Value) > AttachmentDeferralBound + TimeSpan.FromHours(1))
            .Select(pair => pair.Key).ToArray()) firstPull.Remove(key);
    }

    // A transient outcome or a stale Verified: the state goes, its reservation ends, verified files stay for the next pull.
    // Round 2b: a newer pull that already replaced this state keeps its own reservation.
    private void Drop((Guid Peer, Guid Message) key, PullState state, AttachmentStore attachments)
    {
        bool own;
        lock (attachmentGate)
        {
            own = !pulls.TryGetValue(key, out var current) || ReferenceEquals(current, state);
            if (own) pulls.Remove(key);
        }
        if (own) attachments.Release(key.Peer, key.Message);
        DisposeWhenDone(state);
    }

    // Admitted: the state and the deferral entry go, and the folder now counts as admitted bytes.
    private void Finish((Guid Peer, Guid Message) key, PullState state, AttachmentStore attachments)
    {
        lock (attachmentGate) { pulls.Remove(key); firstPull.Remove(key); }
        attachments.Admitted(key.Peer, key.Message);
        DisposeWhenDone(state);
    }

    // A definite refusal: the pull is cancelled and its folder deleted (which ends its reservation). Round 6 (S-017): true when
    // the folder was kept instead because another forward of the message admitted it (AttachmentStore.DeleteInbox).
    private bool Abandon((Guid Peer, Guid Message) key, AttachmentStore attachments)
    {
        PullState? state;
        lock (attachmentGate) { pulls.Remove(key, out state); firstPull.Remove(key); }
        if (state is not null) { state.Cancel.Cancel(); DisposeWhenDone(state); }
        return attachments.DeleteInbox(key.Peer, key.Message);
    }

    // F-8: the linked source is disposed only once its pull has finished using it.
    private void DisposeWhenDone(PullState state) =>
        state.Task.ContinueWith((_, source) =>
        {
            ((CancellationTokenSource)source!).Dispose();
            Interlocked.Decrement(ref AttachmentLinkedSourcesLive);
        }, state.Cancel, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private async Task<PullOutcome> PullAsync(PeerTrust peer, Guid messageId, AttachmentEnvelopeData envelope, string envelopeText,
        AttachmentStore attachments, CancellationToken stop)
    {
        try
        {
            var authorization = Current(peer);
            foreach (var entry in envelope.Attachments)
            {
                if (attachments.HasVerified(peer.BrokerId, messageId, entry)) continue;
                var outcome = await PullFileAsync(peer, authorization, messageId, entry, envelopeText, attachments, stop);
                if (outcome != PullOutcome.Verified) return outcome;
            }
            return PullOutcome.Verified;
        }
        catch (UnauthorizedAccessException) { return PullOutcome.Revoked; }
        // A pull's own defect is that pull's transient failure, bounded by the deferral clock; it never escapes into the route.
        catch (Exception) { return PullOutcome.Transient; }
    }

    private async Task<PullOutcome> PullFileAsync(PeerTrust peer, BrokerStore.PeerAuthorization authorization, Guid messageId,
        AttachmentEntry entry, string envelopeText, AttachmentStore attachments, CancellationToken stop)
    {
        using var client = Client(Advert(peer), wrap: AttachmentHandler);
        client.Timeout = AttachmentFileTimeout;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(AttachmentFileTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, "federation/attachments/" + messageId.ToString("D") + "/" +
            entry.Index.ToString(CultureInfo.InvariantCulture));
        HttpResponseMessage answer;
        try { answer = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token); }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException) { return PullOutcome.Transient; }
        using var response = answer;
        Current(authorization);
        if (response.StatusCode == HttpStatusCode.NotFound) return PullOutcome.Unavailable;
        if (response.StatusCode != HttpStatusCode.OK) return PullOutcome.Transient;
        // P4: the declared size, before any body byte is read. A missing length (chunked) is a mismatch too.
        if (response.Content.Headers.ContentLength != entry.Bytes) return PullOutcome.Mismatch;
        byte[] buffer = new byte[entry.Bytes];
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            int read = 0;
            while (read < buffer.Length)
            {
                int count = await body.ReadAsync(buffer.AsMemory(read), timeout.Token);
                if (count == 0) return PullOutcome.Transient;
                read += count;
                Interlocked.Add(ref AttachmentBodyBytesRead, count);
            }
            // Belt and braces only (F-24): once the Content-Length check passed, HTTP framing ends the body at that length,
            // so this read cannot see an extra byte. Not counted as a guard.
            if (await body.ReadAsync(new byte[1], timeout.Token) != 0) return PullOutcome.Mismatch;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException) { return PullOutcome.Transient; }
        // P6: verified in memory; unverified bytes never reach the disk.
        if (Convert.ToHexStringLower(SHA256.HashData(buffer)) != entry.Sha256) return PullOutcome.Mismatch;
        Current(authorization);
        // Round 2b: the owner may have stopped this broker's attachments during the pull; nothing more is published then.
        if (!attachments.Policy.Allows(store.Peers().SingleOrDefault(candidate => candidate.BrokerId == peer.BrokerId))) return PullOutcome.Revoked;
        try { attachments.PublishVerified(peer.BrokerId, messageId, entry, buffer, envelopeText); }
        // F-6: a quarantine that is not a plain local NTFS folder (or denies access) will not heal on a retry.
        catch (Exception error) when (error is InvalidDataException or UnauthorizedAccessException) { return PullOutcome.QuarantineUnavailable; }
        catch (Exception error) when (error is IOException or ArgumentException or NotSupportedException) { return PullOutcome.Transient; }
        return PullOutcome.Verified;
    }

    private void StopAttachments()
    {
        try { attachmentStop.Cancel(); }
        catch (ObjectDisposedException) { }
    }
}
