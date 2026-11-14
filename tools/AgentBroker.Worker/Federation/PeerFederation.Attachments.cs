using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using AgentBroker.Core;
using AgentBroker.Worker.Attachments;

namespace AgentBroker.Worker.Federation;

// File attachments between paired brokers (lane F, 1.10.5; design: marathon-1105 ITEM-DESIGNS "Lane F" as amended by the
// lead's decisions D-1..D-9 of 2026-11-14). Pull, not push: a message carries an envelope naming each file's size and
// SHA-256, and the RECIPIENT broker fetches every file from the sender broker over the same pinned mutual-TLS channel,
// verifies it in memory, and publishes it into quarantine with a Mark of the Web before the message is admitted. A message is
// never admitted with partial or unverified content.
public sealed partial class PeerFederation
{
    // Null: no attachment store was wired (a test or embedding), so ordinary text is admitted exactly as before and every
    // envelope is refused by policy.
    public AttachmentStore? Attachments { get; init; }

    internal const int MaximumAttachmentPullsPerPeer = 2;
    internal const int MaximumAttachmentPulls = 4;
    internal const int MaximumAttachmentServesPerPeer = 2;
    // D-1: the message route waits this long for a pull, well inside the sender's 10 s forward, then answers a deferral and
    // lets the pull run on; the sender's next forward admits from the verified files.
    internal static readonly TimeSpan AttachmentInlineWait = TimeSpan.FromSeconds(5);
    // D-3: a sender pair's later messages wait behind a deferred one (BrokerStore.BeginDelivery), so deferral is bounded.
    internal static readonly TimeSpan AttachmentDeferralBound = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan AttachmentFileTimeout = TimeSpan.FromSeconds(30);
    private enum PullOutcome { Verified, Mismatch, Unavailable, Transient, Revoked }
    private sealed record PullState(Guid Peer, Task<PullOutcome> Task, DateTimeOffset StartedAt, CancellationTokenSource Cancel);
    private readonly object attachmentGate = new();
    private readonly Dictionary<(Guid Peer, Guid Message), PullState> pulls = new();
    private readonly Dictionary<(Guid Peer, Guid Message), DateTimeOffset> firstDeferral = new();
    private readonly Dictionary<Guid, int> serving = new();
    private readonly CancellationTokenSource attachmentStop = new();
    // Test evidence only: body bytes this broker has read from paired brokers' attachment responses.
    internal long AttachmentBodyBytesRead;

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
            if (!TryEnterServing(caller)) return Results.StatusCode(429);
            try
            {
                var entry = envelope.Attachments[index];
                using var handle = attachments.OpenOutgoing(id, receipt.Message.Text, entry);
                if (handle is null) return Results.NotFound();
                // Recheck after the open, as the message route does after its body read: a revocation in between serves nothing.
                if (RequirePeer(c) != caller) throw new UnauthorizedAccessException();
                c.Response.StatusCode = StatusCodes.Status200OK;
                c.Response.ContentType = "application/octet-stream";
                c.Response.ContentLength = entry.Bytes;
                await using var file = new FileStream(handle, FileAccess.Read, 81920, false);
                await file.CopyToAsync(c.Response.Body, c.RequestAborted);
                return Results.Empty;
            }
            finally { ExitServing(caller); }
        }));

    private bool TryEnterServing(Guid caller)
    {
        lock (attachmentGate)
        {
            int count = serving.GetValueOrDefault(caller);
            if (count >= MaximumAttachmentServesPerPeer) return false;
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
    // to the store; an envelope is admitted only once every file is verified in quarantine.
    private async Task<IResult> AcceptMessageAsync(HttpContext c, Guid caller, BrokerMessage message)
    {
        // R0: a retry of an admitted message never pulls again; the store's idempotent path answers it.
        if (store.FindReceipt(message.Id) is not null) return Results.Json(store.Accept(message, caller), Json);
        var parse = AttachmentEnvelope.TryParse(message.Text, out var envelope);
        if (parse == AttachmentParse.NotEnvelope) return Results.Json(store.Accept(message, caller), Json);
        var attachments = Attachments;
        var peer = store.Peers().SingleOrDefault(candidate => candidate.BrokerId == caller);
        // R3, before anything else: a broker the owner has not allowed learns only that.
        if (attachments is not { Enabled: true } || !attachments.Policy.Allows(peer))
            throw new BrokerException(AttachmentCodes.RefusedByPolicy, "This broker does not accept attachments from that broker.");
        if (parse == AttachmentParse.Invalid)
            throw new BrokerException(AttachmentCodes.EnvelopeInvalid, "The attachment envelope is invalid.");
        var key = (caller, message.Id);
        var state = GetOrStartPull(peer!, message, envelope!, attachments);
        if (state is null) throw Defer(key);
        try { await state.Task.WaitAsync(AttachmentInlineWait, TimeProvider.System, c.RequestAborted); }
        catch (TimeoutException) { throw Defer(key); }
        switch (state.Task.Result)
        {
            case PullOutcome.Verified:
                MessageReceipt admitted;
                try { admitted = store.Accept(message, caller); }
                catch (BrokerException)
                {
                    Forget(key);
                    attachments.DeleteInbox(caller, message.Id);
                    throw;
                }
                Forget(key);
                return Results.Json(admitted, Json);
            case PullOutcome.Mismatch:
                Abandon(key, attachments);
                throw new BrokerException(AttachmentCodes.Mismatch, "An attachment did not match its declared size or SHA-256.");
            case PullOutcome.Unavailable:
                Abandon(key, attachments);
                throw new BrokerException(AttachmentCodes.Unavailable, "The sender has no such attachment.");
            case PullOutcome.Revoked:
                Abandon(key, attachments);
                throw new UnauthorizedAccessException("An explicitly paired TLS identity is required.");
            default:
                // Transient: the next forward starts a fresh pull, within the deferral bound.
                lock (attachmentGate) if (pulls.TryGetValue(key, out var current) && ReferenceEquals(current, state)) pulls.Remove(key);
                throw Defer(key);
        }
    }

    // A deferral, or once the bound has passed since this message's first deferral, the final refusal (D-3).
    private BrokerException Defer((Guid Peer, Guid Message) key)
    {
        DateTimeOffset now = Now, first;
        lock (attachmentGate)
        {
            foreach (var stale in firstDeferral.Where(pair => now - pair.Value > TimeSpan.FromHours(1)).Select(pair => pair.Key).ToArray())
                firstDeferral.Remove(stale);
            if (!firstDeferral.TryGetValue(key, out first))
            {
                if (firstDeferral.Count >= 256) firstDeferral.Remove(firstDeferral.MinBy(pair => pair.Value).Key);
                firstDeferral[key] = first = now;
            }
        }
        if (now - first < AttachmentDeferralBound)
            return new BrokerException(AttachmentCodes.FetchPending, "The attachments are still being fetched; retry later.");
        if (Attachments is { } attachments) Abandon(key, attachments);
        return new BrokerException(AttachmentCodes.FetchFailed, "The attachments could not be fetched in time.");
    }

    private PullState? GetOrStartPull(PeerTrust peer, BrokerMessage message, AttachmentEnvelopeData envelope, AttachmentStore attachments)
    {
        var key = (peer.BrokerId, message.Id);
        lock (attachmentGate)
        {
            if (pulls.TryGetValue(key, out var existing)) return existing;
            foreach (var done in pulls.Where(pair => pair.Value.Task.IsCompleted && Now - pair.Value.StartedAt > AttachmentStore.OrphanGrace)
                .Select(pair => pair.Key).ToArray()) pulls.Remove(done);
            int running = pulls.Values.Count(pull => !pull.Task.IsCompleted);
            int runningForPeer = pulls.Values.Count(pull => pull.Peer == peer.BrokerId && !pull.Task.IsCompleted);
            if (running >= MaximumAttachmentPulls || runningForPeer >= MaximumAttachmentPullsPerPeer) return null;
            // R4: refused at once, not deferred; quarantine space frees on a retention scale, not a retry scale.
            if (!attachments.QuarantineHasRoom(envelope.TotalBytes))
                throw new BrokerException(AttachmentCodes.QuarantineFull, "The attachment quarantine is full.");
            var cancel = CancellationTokenSource.CreateLinkedTokenSource(attachmentStop.Token);
            var state = new PullState(peer.BrokerId, Task.Run(() => PullAsync(peer, message.Id, envelope, attachments, cancel.Token)), Now, cancel);
            pulls[key] = state;
            return state;
        }
    }

    private void Forget((Guid Peer, Guid Message) key)
    {
        lock (attachmentGate) { pulls.Remove(key); firstDeferral.Remove(key); }
    }

    private void Abandon((Guid Peer, Guid Message) key, AttachmentStore attachments)
    {
        PullState? state;
        lock (attachmentGate) { pulls.Remove(key, out state); firstDeferral.Remove(key); }
        state?.Cancel.Cancel();
        attachments.DeleteInbox(key.Peer, key.Message);
    }

    private async Task<PullOutcome> PullAsync(PeerTrust peer, Guid messageId, AttachmentEnvelopeData envelope, AttachmentStore attachments,
        CancellationToken stop)
    {
        try
        {
            var authorization = Current(peer);
            foreach (var entry in envelope.Attachments)
            {
                if (attachments.HasVerified(peer.BrokerId, messageId, entry)) continue;
                var outcome = await PullFileAsync(peer, authorization, messageId, entry, attachments, stop);
                if (outcome != PullOutcome.Verified) return outcome;
            }
            return PullOutcome.Verified;
        }
        catch (UnauthorizedAccessException) { return PullOutcome.Revoked; }
        // A pull's own defect is that pull's transient failure, bounded by the deferral clock; it never escapes into the route.
        catch (Exception) { return PullOutcome.Transient; }
    }

    private async Task<PullOutcome> PullFileAsync(PeerTrust peer, BrokerStore.PeerAuthorization authorization, Guid messageId,
        AttachmentEntry entry, AttachmentStore attachments, CancellationToken stop)
    {
        using var client = Client(Advert(peer));
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
            if (await body.ReadAsync(new byte[1], timeout.Token) != 0) return PullOutcome.Mismatch;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException) { return PullOutcome.Transient; }
        // P6: verified in memory; unverified bytes never reach the disk.
        if (Convert.ToHexStringLower(SHA256.HashData(buffer)) != entry.Sha256) return PullOutcome.Mismatch;
        Current(authorization);
        try { attachments.PublishVerified(peer.BrokerId, messageId, entry, buffer); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException) { return PullOutcome.Transient; }
        return PullOutcome.Verified;
    }

    private void StopAttachments()
    {
        try { attachmentStop.Cancel(); }
        catch (ObjectDisposedException) { }
    }
}
