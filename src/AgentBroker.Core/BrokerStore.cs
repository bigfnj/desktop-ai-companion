using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentBroker.Core;

/// <summary>
/// Single-writer durable broker state. The owner lock belongs to this object, not a thread.
/// Each mutation writes a replacement before publishing in-memory state. No message text is logged.
/// </summary>
public sealed class BrokerStore : IDisposable
{
    public sealed class PeerAuthorization
    {
        internal readonly Guid BrokerId, Stamp;
        internal PeerAuthorization(Guid brokerId, Guid stamp) { BrokerId = brokerId; Stamp = stamp; }
    }
    private sealed record State(int Version, Guid BrokerId, List<PeerTrust> Peers, List<MessageReceipt> Messages);
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private readonly object gate = new();
    private readonly string path;
    private readonly FileStream ownership;
    private readonly BrokerLimits limits;
    private readonly TimeProvider clock;
    private readonly long maximumStateBytes;
    private readonly Dictionary<SessionAddress, BrokerSession> sessions = new();
    // Generic MCP mailboxes only: when the owner last registered or renewed. Process-local like the
    // sessions themselves (never on the wire, never in State), so a worker restart still drops them.
    private readonly Dictionary<SessionAddress, DateTimeOffset> mailboxActivity = new();
    private readonly Dictionary<Guid, Guid> peerAuthorizations = new();
    // Completed and replaced by every Commit. A waiter captures it BEFORE checking state, so a commit
    // between its check and its wait has already completed the task it awaits. Never awaited under gate.
    private TaskCompletionSource changeSignal = NewSignal();
    private State state;
    private bool disposed;

    public BrokerStore(string directory, BrokerLimits? limits = null, TimeProvider? clock = null)
    {
        this.limits = limits ?? new();
        this.clock = clock ?? TimeProvider.System;
        if (this.limits.MaximumMessages <= 0 || this.limits.MaximumTextBytes <= 0 ||
            this.limits.MaximumSessions <= 0 || this.limits.MaximumPeers <= 0 ||
            this.limits.MaximumPendingPerSession <= 0 || this.limits.MaximumReplies < 0 ||
            this.limits.DefaultMessageLifetimeMinutes is < 1 or > 1440)
            throw new ArgumentOutOfRangeException(nameof(limits), "Broker limits must be positive (reply budget may be zero); default message lifetime must be 1..1440 minutes.");
        try { maximumStateBytes = checked((long)this.limits.MaximumMessages * (this.limits.MaximumTextBytes * 6L + 4096) + 1024 * 1024); }
        catch (OverflowException) { throw new ArgumentOutOfRangeException(nameof(limits), "Broker state size limit overflows."); }
        System.IO.Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "broker-state.json");
        ownership = new FileStream(Path.Combine(directory, "broker-owner.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            bool existed = File.Exists(path);
            state = existed
                ? JsonSerializer.Deserialize<State>(ReadBounded(path), Json)
                    ?? throw new InvalidDataException("Broker state is empty.")
                : new State(1, Guid.NewGuid(), [], []);
            ValidateState();
            foreach (var peer in state.Peers) peerAuthorizations.Add(peer.BrokerId, Guid.NewGuid());
            // Submission interrupted between adapter admission and receipt is ambiguous. Never
            // blindly re-submit a native client prompt after a crash and claim exactly-once delivery.
            var recovered = state.Messages.Select(r => r.Status is MessageStatus.Submitting or MessageStatus.Submitted
                ? r with { Status = MessageStatus.Uncertain, UpdatedAt = Now, Reason = "restart_during_submission" }
                : r).ToList();
            if (!existed || !recovered.SequenceEqual(state.Messages)) Commit(state with { Messages = recovered });
        }
        catch (JsonException ex) { ownership.Dispose(); throw new InvalidDataException("Broker state is malformed. It was preserved for recovery.", ex); }
        catch { ownership.Dispose(); throw; }
    }

    public Guid BrokerId { get { lock (gate) { EnsureOpen(); return state.BrokerId; } } }
    private DateTimeOffset Now => clock.GetUtcNow();

    public void Register(BrokerSession session)
    {
        lock (gate)
        {
            EnsureOpen();
            if (session is null || session.Address is null) Fail("invalid_session");
            session!.Address.Validate();
            if (session.Address.BrokerId != state.BrokerId) Fail("foreign_registration");
            if (string.IsNullOrWhiteSpace(session.Adapter) || session.Adapter.Length > 80 || session.Adapter.Any(char.IsControl) ||
                session.Label is null || session.Label.Length > 160 || session.Label.Any(char.IsControl) ||
                // Away is the store's own display state for a retained mailbox, never a claim.
                !Enum.IsDefined(session.Activity) || session.Activity == SessionActivity.Away ||
                (session.Capabilities & ~(SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply | SessionCapabilities.NativeIdleGuard)) != 0 ||
                session.Capabilities.HasFlag(SessionCapabilities.NativeIdleGuard) &&
                    !session.Capabilities.HasFlag(SessionCapabilities.Receive | SessionCapabilities.WakeIdle))
                Fail("invalid_session");
            if (session.LeaseUntil <= Now || session.LeaseUntil > Now + BrokerLimits.MaximumSessionLease)
                Fail("invalid_lease");
            TrimSessions();
            var displaced = sessions.Keys.Where(a => a.SessionId == session.Address.SessionId && a != session.Address).ToArray();
            // At capacity a new registration may take a retained mailbox's slot (lapsed presence), never
            // a present session's. With only present sessions left the refusal stays session_limit.
            if (!sessions.ContainsKey(session.Address) && sessions.Count - displaced.Length >= limits.MaximumSessions &&
                EvictRetained(displaced) is null) Fail("session_limit");
            // A new incarnation supersedes only the same exact vendor session ID, never a title/cwd match.
            foreach (var previous in displaced)
                Forget(previous);
            sessions[session.Address] = session;
            if (session.Adapter == "mcp") mailboxActivity[session.Address] = Now;
            else mailboxActivity.Remove(session.Address);
        }
    }

    public IReadOnlyList<BrokerSession> Directory(bool forPeer = false)
    {
        lock (gate)
        {
            EnsureOpen(); TrimSessions();
            var now = Now;
            // Peers see presence only, exactly as before mailboxes existed: a lapsed lease never leaves
            // this broker, so an older peer never meets Away and no wire record changes.
            if (forPeer) return sessions.Values.Where(s => s.LeaseUntil > now && !s.IsPrivate).ToArray();
            // Locally a retained mailbox is listed as Away with its last presence lease, now in the past.
            return sessions.Values.Where(s => s.LeaseUntil > now || Retained(s))
                .Select(s => s.LeaseUntil > now ? s : s with { Activity = SessionActivity.Away }).ToArray();
        }
    }

    // Only an existing addressable polling inbox can be extended: a present one, or a retained mailbox
    // whose owner is back (that call restores presence). Keep the read and replacement under the store
    // lock so retention ending, disconnection and a new incarnation cannot be undone.
    public BrokerSession? RenewPollingSession(SessionAddress address)
    {
        lock (gate)
        {
            EnsureOpen(); address.Validate();
            var now = Now;
            if (!sessions.TryGetValue(address, out var session) || session.Adapter != "mcp" || !Addressable(address))
                return null;
            mailboxActivity[address] = now;
            var until = now.AddMinutes(1);
            if (until <= session.LeaseUntil) return session;
            var renewed = session with { LeaseUntil = until };
            sessions[address] = renewed;
            return renewed;
        }
    }

    public void Disconnect(SessionAddress address)
    {
        lock (gate) { EnsureOpen(); Forget(address); }
    }

    /// <summary>A present session, or a retained generic MCP mailbox. Native sessions only while live.</summary>
    public bool IsAddressable(SessionAddress address)
    {
        lock (gate) { EnsureOpen(); return Addressable(address); }
    }

    /// <summary>Frees one registration slot by evicting a retained mailbox; null when none is retained.</summary>
    public SessionAddress? EvictRetainedMailbox()
    {
        lock (gate) { EnsureOpen(); TrimSessions(); return EvictRetained([]); }
    }

    /// <summary>
    /// Applies a local privacy or pause choice to a retained mailbox. Registering would need a live lease,
    /// and the owner's choice is not the agent's activity, so presence and retention stay as they were.
    /// </summary>
    public void ApplyRetainedPreference(BrokerSession overlay)
    {
        lock (gate)
        {
            EnsureOpen();
            if (overlay is null || overlay.Address is null) Fail("invalid_session");
            if (!sessions.TryGetValue(overlay!.Address, out var current) || !Retained(current)) Fail("recipient_unavailable");
            if (!Enum.IsDefined(overlay.Activity) || overlay.Activity is SessionActivity.Away or SessionActivity.Disconnected)
                Fail("invalid_session");
            sessions[overlay.Address] = current! with { IsPrivate = overlay.IsPrivate, Activity = overlay.Activity };
        }
    }

    /// <summary>
    /// The task the next committed change completes. Capture it BEFORE checking state, check, then await
    /// it outside every lock; a commit in between has already completed it, so no change is missed.
    /// </summary>
    public Task ChangeSignal()
    {
        lock (gate) { EnsureOpen(); return changeSignal.Task; }
    }

    public IReadOnlyList<PeerTrust> Peers()
    {
        lock (gate) { EnsureOpen(); return state.Peers.ToArray(); }
    }

    // A location refresh must not invalidate an in-flight trusted read, but revocation and a
    // later pairing must. These process-local stamps never survive an outstanding operation.
    public PeerAuthorization? CapturePeerAuthorization(PeerTrust peer)
    {
        lock (gate)
        {
            EnsureOpen();
            return state.Peers.Any(p => p == peer) && peerAuthorizations.TryGetValue(peer.BrokerId, out var stamp)
                ? new(peer.BrokerId, stamp) : null;
        }
    }

    public bool IsPeerAuthorized(PeerAuthorization authorization)
    {
        lock (gate)
        {
            EnsureOpen();
            return peerAuthorizations.TryGetValue(authorization.BrokerId, out var stamp) && stamp == authorization.Stamp;
        }
    }

    // The transport supplies a pinned, verified address. Discovery itself cannot authorize a peer.
    public bool TryRefreshPeerEndpoint(PeerTrust expected, PeerAuthorization authorization, string endpoint)
    {
        lock (gate)
        {
            EnsureOpen();
            if (authorization.BrokerId != expected.BrokerId || !IsPeerAuthorized(authorization)) return false;
            var current = state.Peers.Find(p => p.BrokerId == expected.BrokerId);
            if (!ReferenceEquals(current, expected)) return false;
            var refreshed = expected with { Endpoint = endpoint };
            if (!ValidPeer(refreshed)) Fail("invalid_peer");
            if (endpoint == expected.Endpoint) return true;
            Commit(state with { Peers = state.Peers.Select(p => ReferenceEquals(p, expected) ? refreshed : p).ToList() });
            return true;
        }
    }

    // The caller is the local control boundary after an explicit user pairing confirmation.
    public void AuthorizePeer(PeerTrust peer)
    {
        lock (gate)
        {
            EnsureOpen();
            if (!ValidPeer(peer)) Fail("invalid_peer");
            var existing = state.Peers.Find(p => p.BrokerId == peer.BrokerId);
            if (existing is not null && !string.Equals(existing.CertificateSha256, peer.CertificateSha256, StringComparison.OrdinalIgnoreCase))
                Fail("peer_identity_changed");
            var peers = state.Peers.Where(p => p.BrokerId != peer.BrokerId).ToList();
            if (peers.Count >= limits.MaximumPeers) Fail("peer_limit");
            peers.Add(peer);
            Commit(state with { Peers = peers });
            peerAuthorizations[peer.BrokerId] = Guid.NewGuid();
        }
    }

    public void RevokePeer(Guid brokerId)
    {
        lock (gate)
        {
            EnsureOpen();
            Commit(state with
            {
                Peers = state.Peers.Where(p => p.BrokerId != brokerId).ToList(),
                Messages = state.Messages.Select(r =>
                    r.Message.Sender.BrokerId != brokerId && r.Message.Recipient.BrokerId != brokerId ? r :
                    r.Status is MessageStatus.Submitting or MessageStatus.Submitted
                        ? r with { Status = MessageStatus.Uncertain, Reason = "peer_revoked_during_submission", UpdatedAt = Now }
                        : IsPending(r.Status) ? r with { Status = MessageStatus.Refused, Reason = "peer_revoked", UpdatedAt = Now } : r).ToList()
            });
            peerAuthorizations.Remove(brokerId);
        }
    }

    /// <param name="authenticatedBroker">Identity from the transport, never a field trusted from a payload.</param>
    public MessageReceipt Accept(BrokerMessage message, Guid authenticatedBroker)
    {
        lock (gate)
        {
            EnsureOpen();
            if (message is null || message.Sender is null || message.Recipient is null) Fail("invalid_message");
            message!.Sender.Validate(); message.Recipient.Validate();
            if (authenticatedBroker != message.Sender.BrokerId) Fail("sender_mismatch");
            if (authenticatedBroker != state.BrokerId && !state.Peers.Any(p => p.BrokerId == authenticatedBroker))
                Fail("unpaired_peer");
            // An authenticated retry can recover its receipt after a recipient disconnect/restart
            // or message expiry. It must not create a new delivery or require a live old incarnation.
            var existing = state.Messages.Find(r => r.Message.Id == message.Id);
            if (existing is not null)
            {
                if (existing.Message != message) Fail("message_id_collision");
                if (IsPending(existing.Status) && existing.Message.ExpiresAt <= Now)
                {
                    var expired = existing with { Status = MessageStatus.Expired, UpdatedAt = Now, Reason = "expired" };
                    Commit(state with { Messages = state.Messages.Select(r => r == existing ? expired : r).ToList() });
                    return expired;
                }
                return existing;
            }
            ValidateMessage(message);
            // Only direct federation. This rejects forwarding a third broker's message or a peer
            // asking us to route onward to another peer.
            if (authenticatedBroker != state.BrokerId && message.Recipient.BrokerId != state.BrokerId) Fail("transit_refused");
            if (message.Recipient.BrokerId != state.BrokerId && !state.Peers.Any(p => p.BrokerId == message.Recipient.BrokerId))
                Fail("unpaired_peer");
            // A retained generic mailbox (lapsed presence) still sends and receives; native sessions must be live.
            if (authenticatedBroker == state.BrokerId && !Addressable(message.Sender)) Fail("sender_unavailable");
            if (authenticatedBroker == state.BrokerId && message.ReplyTo is not null &&
                !sessions[message.Sender].Capabilities.HasFlag(SessionCapabilities.Reply)) Fail("sender_cannot_reply");
            if (message.Recipient.BrokerId == state.BrokerId)
            {
                if (!sessions.TryGetValue(message.Recipient, out var target) || !Addressable(message.Recipient)) Fail("recipient_unavailable");
                if (authenticatedBroker != state.BrokerId && target!.IsPrivate) Fail("private_session");
                if (!target!.Capabilities.HasFlag(SessionCapabilities.Receive)) Fail("recipient_cannot_receive");
            }
            if (message.ReplyTo is Guid replyId)
            {
                var parent = state.Messages.Find(r => r.Message.Id == replyId)?.Message;
                if (parent is null || parent.ConversationId != message.ConversationId ||
                    parent.Recipient != message.Sender || parent.Sender != message.Recipient ||
                    parent.RemainingReplies <= 0 || message.RemainingReplies != parent.RemainingReplies - 1)
                    Fail("invalid_reply");
            }
            else if (message.RemainingReplies != limits.MaximumReplies) Fail("invalid_reply_budget");
            var retained = Expire(state.Messages);
            if (retained.Count >= limits.MaximumMessages) Fail("message_limit");
            if (retained.Count(r => r.Message.Recipient == message.Recipient && IsOutstanding(r.Status)) >= limits.MaximumPendingPerSession)
                Fail("recipient_queue_full");
            var receipt = new MessageReceipt(message, MessageStatus.Accepted, Now);
            retained.Add(receipt); Commit(state with { Messages = retained });
            return receipt;
        }
    }

    /// <summary>
    /// Creates a reply once under the same state lock as admission. Stable-ID retries reuse the
    /// originally persisted timestamps; they cannot turn into a new payload merely because time passed.
    /// The parent's expiry is its delivery deadline, not a deadline for completing and replying to work.
    /// </summary>
    public MessageReceipt AcceptReply(Guid originalMessageId, Guid messageId, string text, Guid authenticatedBroker)
    {
        lock (gate)
        {
            EnsureOpen();
            var parent = state.Messages.Find(receipt => receipt.Message.Id == originalMessageId)?.Message;
            if (parent is null) Fail("invalid_reply");
            if (authenticatedBroker != parent.Recipient.BrokerId) Fail("sender_mismatch");
            if (authenticatedBroker != state.BrokerId && !state.Peers.Any(peer => peer.BrokerId == authenticatedBroker))
                Fail("unpaired_peer");
            var existing = state.Messages.Find(receipt => receipt.Message.Id == messageId);
            if (existing is not null)
            {
                var reply = existing.Message;
                if (reply.ReplyTo != parent.Id || reply.ConversationId != parent.ConversationId ||
                    reply.Sender != parent.Recipient || reply.Recipient != parent.Sender || reply.Text != text ||
                    parent.RemainingReplies <= 0 || reply.RemainingReplies != parent.RemainingReplies - 1)
                    Fail("message_id_collision");
                // Reentrant admission rechecks transport authority and returns/ages the same receipt.
                // As with Accept retries, a disconnected old incarnation does not create a delivery.
                return Accept(reply, authenticatedBroker);
            }
            if (parent.RemainingReplies <= 0) Fail("invalid_reply");
            var now = Now;
            return Accept(new(messageId, parent.ConversationId, parent.Id, parent.Recipient, parent.Sender,
                text, now, now.AddMinutes(limits.DefaultMessageLifetimeMinutes), parent.RemainingReplies - 1), authenticatedBroker);
        }
    }

    /// <summary>
    /// Creates or recovers a new (non-reply) message from caller-supplied fields only. A retry compares
    /// sender, recipient, text, the absence of ReplyTo and, when given, the conversation; never a
    /// timestamp, so a client that regenerates its clock fields on retry recovers its receipt instead of
    /// colliding. Modelled on AcceptReply: one lock, timestamps fixed by the first admission.
    /// </summary>
    public MessageReceipt Send(SessionAddress sender, SessionAddress recipient, Guid messageId, string text,
        Guid? conversationId, int lifetimeMinutes, Guid authenticatedBroker)
    {
        lock (gate)
        {
            EnsureOpen();
            if (sender is null || recipient is null || text is null || messageId == Guid.Empty ||
                conversationId == Guid.Empty || lifetimeMinutes < 0) Fail("invalid_message");
            sender!.Validate(); recipient!.Validate();
            var existing = state.Messages.Find(receipt => receipt.Message.Id == messageId);
            if (existing is not null)
            {
                var sent = existing.Message;
                if (sent.Sender != sender || sent.Recipient != recipient || sent.Text != text || sent.ReplyTo is not null ||
                    conversationId is Guid conversation && sent.ConversationId != conversation)
                    Fail("message_id_collision");
                // The requested lifetime is ignored on a retry. Reentrant admission rechecks transport
                // authority and returns (or ages) the stored receipt, exactly as a reply retry does.
                return Accept(sent, authenticatedBroker);
            }
            var now = Now;
            var lifetime = TimeSpan.FromMinutes(lifetimeMinutes == 0 ? limits.DefaultMessageLifetimeMinutes : lifetimeMinutes);
            if (lifetime > BrokerLimits.MaximumLifetime) lifetime = BrokerLimits.MaximumLifetime;
            return Accept(new(messageId, conversationId ?? messageId, null, sender, recipient, text, now, now + lifetime,
                limits.MaximumReplies), authenticatedBroker);
        }
    }

    public IReadOnlyList<MessageReceipt> Messages(SessionAddress participant)
    {
        lock (gate)
        {
            EnsureOpen();
            participant.Validate();
            var messages = Expire(state.Messages);
            if (!messages.SequenceEqual(state.Messages)) Commit(state with { Messages = messages });
            return state.Messages.Where(r => r.Message.Sender == participant || r.Message.Recipient == participant).ToArray();
        }
    }

    public MessageReceipt? BeginDelivery(IReadOnlySet<Guid>? excludedMessageIds = null)
    {
        lock (gate)
        {
            EnsureOpen();
            var messages = Expire(state.Messages);
            // Re-evaluate mutable local policy immediately before claiming. Prior acceptance never
            // grants a permanent right to a session that became private or lost its receive capability.
            for (int i = 0; i < messages.Count; i++)
            {
                var r = messages[i];
                if (r.Status != MessageStatus.Accepted || r.Message.Recipient.BrokerId != state.BrokerId ||
                    !sessions.TryGetValue(r.Message.Recipient, out var target) || !Live(target.Address)) continue;
                string? refusal = r.Message.Sender.BrokerId != state.BrokerId && target.IsPrivate ? "private_session" :
                    !target.Capabilities.HasFlag(SessionCapabilities.Receive) ? "recipient_cannot_receive" : null;
                if (refusal is not null) messages[i] = r with { Status = MessageStatus.Refused, Reason = refusal, UpdatedAt = Now };
            }
            var inflight = messages.Where(r => r.Status == MessageStatus.Submitting).Select(r => r.Message.Recipient).ToHashSet();
            var index = messages.FindIndex(r => r.Status == MessageStatus.Accepted && excludedMessageIds?.Contains(r.Message.Id) != true && !inflight.Contains(r.Message.Recipient) &&
                (r.Message.Recipient.BrokerId != state.BrokerId || Ready(r.Message.Recipient)));
            MessageReceipt? next = null;
            if (index >= 0)
            {
                next = messages[index] with { Status = MessageStatus.Submitting, UpdatedAt = Now, Attempts = messages[index].Attempts + 1 };
                messages[index] = next;
            }
            if (!messages.SequenceEqual(state.Messages)) Commit(state with { Messages = messages });
            return next;
        }
    }

    public MessageReceipt CompleteDelivery(Guid messageId, MessageStatus outcome, string? reason = null)
    {
        lock (gate)
        {
            EnsureOpen();
            if (outcome is not (MessageStatus.Submitted or MessageStatus.Delivered or MessageStatus.Held or MessageStatus.Refused or MessageStatus.Uncertain))
                Fail("invalid_outcome");
            if (reason?.Length > 80 || reason?.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_') == true) Fail("invalid_reason");
            var messages = state.Messages.ToList();
            var index = messages.FindIndex(r => r.Message.Id == messageId);
            if (index < 0 || messages[index].Status != MessageStatus.Submitting) Fail("message_not_submitting");
            var next = messages[index] with { Status = outcome, Reason = reason, UpdatedAt = Now };
            messages[index] = next; Commit(state with { Messages = messages }); return next;
        }
    }

    public MessageReceipt? FindReceipt(Guid messageId)
    {
        lock (gate) { EnsureOpen(); return state.Messages.Find(r => r.Message.Id == messageId); }
    }

    /// <summary>Persisted outbound receipt reconciliation, independent of volatile sender registrations.</summary>
    public IReadOnlyList<MessageReceipt> OutboundAwaitingReceipt(Guid peerBrokerId, int max = 64, Guid? afterMessageId = null)
    {
        lock (gate)
        {
            EnsureOpen();
            if (peerBrokerId == Guid.Empty || peerBrokerId == state.BrokerId) Fail("invalid_peer");
            if (max is < 1 or > 64) Fail("invalid_limit");
            var messages = Expire(state.Messages);
            if (!messages.SequenceEqual(state.Messages)) Commit(state with { Messages = messages });
            var eligible = state.Messages.Where(r => r.Message.Sender.BrokerId == state.BrokerId && r.Message.Recipient.BrokerId == peerBrokerId &&
                r.Status is MessageStatus.Submitted or MessageStatus.Uncertain or MessageStatus.Held).ToArray();
            int previous = afterMessageId is Guid cursor ? Array.FindIndex(eligible, r => r.Message.Id == cursor) : -1;
            if (previous < 0) return eligible.Take(max).ToArray(); // An expired/settled cursor restarts safely.
            return eligible.Skip(previous + 1).Concat(eligible.Take(previous + 1)).Take(max).ToArray();
        }
    }

    /// <summary>
    /// Releases a claimed message only when the adapter has authoritative proof that no model input
    /// was submitted. Generic errors/timeouts must complete Uncertain instead. Never releases a
    /// Submitted/Uncertain claim, so an ambiguous native input cannot be blindly replayed.
    /// </summary>
    public MessageReceipt ReleaseUnsubmittedClaim(Guid messageId, string? reason = null)
    {
        lock (gate)
        {
            EnsureOpen();
            reason ??= "deferred_not_submitted";
            if (string.IsNullOrEmpty(reason) || reason.Length > 80 || reason.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
                Fail("invalid_reason");
            var messages = state.Messages.ToList();
            int index = messages.FindIndex(r => r.Message.Id == messageId);
            if (index < 0 || messages[index].Status != MessageStatus.Submitting) Fail("message_not_submitting");
            var previous = messages[index];
            var next = previous.Message.ExpiresAt <= Now
                ? previous with { Status = MessageStatus.Expired, UpdatedAt = Now, Reason = "expired" }
                : previous with { Status = MessageStatus.Accepted, UpdatedAt = Now, Reason = reason, Attempts = 0 };
            messages[index] = next; Commit(state with { Messages = messages }); return next;
        }
    }

    /// <summary>Records an explicit recipient receipt. Transport admission alone is Submitted.</summary>
    public MessageReceipt RecordRecipientReceipt(Guid messageId, SessionAddress recipient,
        Guid authenticatedBroker, MessageStatus outcome)
    {
        lock (gate)
        {
            EnsureOpen(); recipient.Validate();
            if (recipient.BrokerId != authenticatedBroker) Fail("recipient_mismatch");
            if (authenticatedBroker != state.BrokerId && !state.Peers.Any(p => p.BrokerId == authenticatedBroker)) Fail("unpaired_peer");
            if (authenticatedBroker == state.BrokerId &&
                (!Addressable(recipient) || !sessions[recipient].Capabilities.HasFlag(SessionCapabilities.Receive))) Fail("recipient_unavailable");
            if (outcome is not (MessageStatus.Delivered or MessageStatus.Held or MessageStatus.Refused)) Fail("invalid_outcome");
            var messages = state.Messages.ToList();
            int index = messages.FindIndex(r => r.Message.Id == messageId);
            if (index < 0 || messages[index].Message.Recipient != recipient) Fail("recipient_mismatch");
            var previous = messages[index];
            if (previous.Status == outcome) return previous;
            if (previous.Status is MessageStatus.Delivered or MessageStatus.Refused or MessageStatus.Expired) Fail("receipt_final");
            if (previous.Status == MessageStatus.Accepted && previous.Message.ExpiresAt <= Now) Fail("message_expired");
            var next = previous with { Status = outcome, UpdatedAt = Now, Reason = "recipient_receipt", Attempts = 1 };
            messages[index] = next; Commit(state with { Messages = messages }); return next;
        }
    }

    private bool Ready(SessionAddress address) => Live(address) &&
        (sessions[address].Activity == SessionActivity.Idle || sessions[address].Activity == SessionActivity.Unknown &&
            sessions[address].Capabilities.HasFlag(SessionCapabilities.NativeIdleGuard)) &&
        sessions[address].Capabilities.HasFlag(SessionCapabilities.WakeIdle | SessionCapabilities.Receive);
    private bool Live(SessionAddress address) => sessions.TryGetValue(address, out var s) && Live(s);
    private bool Live(BrokerSession s) => s.LeaseUntil > Now && s.Activity != SessionActivity.Disconnected;
    // A generic MCP mailbox whose presence lapsed stays addressable for MailboxRetention after its owner's
    // last call. Native sessions never are: their adapter re-observes a live vendor session, so a lapsed
    // native lease means that session is gone, and waking it must stay impossible.
    private bool Retained(BrokerSession s) => s.Adapter == "mcp" && s.Activity != SessionActivity.Disconnected &&
        s.LeaseUntil <= Now && mailboxActivity.TryGetValue(s.Address, out var seen) && seen + BrokerLimits.MailboxRetention > Now;
    private bool Addressable(SessionAddress address) => sessions.TryGetValue(address, out var s) && (Live(s) || Retained(s));
    private void TrimSessions()
    {
        // A lapsed native lease or an ended retention goes. A disconnected activity with a live lease
        // stays until that lease lapses, exactly as before mailboxes existed.
        foreach (var key in sessions.Where(p => p.Value.LeaseUntil <= Now && !Retained(p.Value)).Select(p => p.Key).ToArray()) Forget(key);
        while (sessions.Values.Count(Retained) > BrokerLimits.MaximumRetainedMailboxes && EvictRetained([]) is not null) { }
    }
    // The oldest retained mailbox with nothing outstanding goes first, then the oldest retained one; a
    // present session is never a candidate. Its persisted messages stay: re-registering the same clientId
    // and incarnation addresses them again.
    private SessionAddress? EvictRetained(IReadOnlyCollection<SessionAddress> keep)
    {
        var victim = sessions.Values.Where(s => Retained(s) && !keep.Contains(s.Address))
            .OrderBy(s => state.Messages.Any(r => r.Message.Recipient == s.Address && IsOutstanding(r.Status) && r.Message.ExpiresAt > Now))
            .ThenBy(s => mailboxActivity[s.Address]).FirstOrDefault();
        if (victim is null) return null;
        Forget(victim.Address);
        return victim.Address;
    }
    private void Forget(SessionAddress address) { sessions.Remove(address); mailboxActivity.Remove(address); }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private List<MessageReceipt> Expire(List<MessageReceipt> messages) => messages
        .Where(r => r.Message.ExpiresAt > Now - TimeSpan.FromDays(1))
        .Select(r => IsPending(r.Status) && r.Message.ExpiresAt <= Now
            ? r with { Status = MessageStatus.Expired, Reason = "expired", UpdatedAt = Now }
            : r.Status == MessageStatus.Submitted && r.Message.ExpiresAt <= Now
                ? r with { Status = MessageStatus.Uncertain, Reason = "receipt_unconfirmed", UpdatedAt = Now } : r).ToList();
    private static bool IsPending(MessageStatus status) => status is MessageStatus.Accepted or MessageStatus.Held;
    private static bool IsOutstanding(MessageStatus status) => IsPending(status) || status is MessageStatus.Submitting or MessageStatus.Submitted or MessageStatus.Uncertain;
    private void ValidateMessage(BrokerMessage m)
    {
        if (!ValidMessageShape(m) ||
            string.IsNullOrWhiteSpace(m.Text) || Encoding.UTF8.GetByteCount(m.Text) > limits.MaximumTextBytes ||
            m.Text.Contains('\0') || m.CreatedAt > Now + TimeSpan.FromMinutes(1) || m.CreatedAt < Now - BrokerLimits.MaximumLifetime ||
            m.ExpiresAt <= Now || m.ExpiresAt <= m.CreatedAt || m.ExpiresAt > m.CreatedAt + BrokerLimits.MaximumLifetime ||
            m.RemainingReplies < 0 || m.RemainingReplies > limits.MaximumReplies)
            Fail("invalid_message");
    }
    private void ValidateState()
    {
        if (state.Version != 1 || state.BrokerId == Guid.Empty || state.Peers is null || state.Messages is null ||
            state.Peers.Count > limits.MaximumPeers || state.Messages.Count > limits.MaximumMessages ||
            state.Peers.Any(p => p is null || !ValidPeer(p)) ||
            state.Messages.Any(r => r is null || !ValidReceipt(r)) ||
            state.Peers.Select(p => p.BrokerId).Distinct().Count() != state.Peers.Count ||
            state.Messages.Select(p => p.Message.Id).Distinct().Count() != state.Messages.Count)
            throw new InvalidDataException("Broker state is incompatible or invalid. It was preserved for recovery.");
    }
    private bool ValidPeer(PeerTrust? peer) => peer is not null && peer.BrokerId != Guid.Empty && peer.BrokerId != state.BrokerId &&
        peer.Name is not null && peer.Name.Length <= 160 && !peer.Name.Any(char.IsControl) &&
        Uri.TryCreate(peer.Endpoint, UriKind.Absolute, out var endpoint) && endpoint.Scheme == "https" &&
        string.IsNullOrEmpty(endpoint.UserInfo) && endpoint.AbsolutePath == "/" && string.IsNullOrEmpty(endpoint.Query) &&
        string.IsNullOrEmpty(endpoint.Fragment) && peer.CertificateSha256 is not null && peer.CertificateSha256.Length == 64 &&
        peer.CertificateSha256.All(Uri.IsHexDigit);

    private bool ValidMessageShape(BrokerMessage? m)
    {
        if (m is null || m.Sender is null || m.Recipient is null || m.Id == Guid.Empty || m.ConversationId == Guid.Empty ||
            m.Sender == m.Recipient || string.IsNullOrWhiteSpace(m.Text) || m.Text.Contains('\0') ||
            Encoding.UTF8.GetByteCount(m.Text) > limits.MaximumTextBytes || m.ExpiresAt <= m.CreatedAt ||
            m.ExpiresAt - m.CreatedAt > BrokerLimits.MaximumLifetime || m.RemainingReplies < 0 || m.RemainingReplies > limits.MaximumReplies ||
            m.ReplyTo == Guid.Empty || m.ReplyTo == m.Id) return false;
        try { m.Sender.Validate(); m.Recipient.Validate(); return true; }
        catch (BrokerException) { return false; }
    }

    private bool ValidReceipt(MessageReceipt receipt)
    {
        if (!ValidMessageShape(receipt.Message) ||
            (receipt.Message.Sender.BrokerId != state.BrokerId && receipt.Message.Recipient.BrokerId != state.BrokerId) ||
            !Enum.IsDefined(receipt.Status) || receipt.Attempts is < 0 or > 1 ||
            receipt.Reason?.Length > 80 || receipt.Reason?.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_') == true) return false;
        // Schema 1 never requeues a claimed message. Delivery is reconciled, never blindly retried.
        return receipt.Status switch
        {
            MessageStatus.Accepted => receipt.Attempts == 0,
            MessageStatus.Submitting or MessageStatus.Submitted or MessageStatus.Delivered or MessageStatus.Held or MessageStatus.Uncertain => receipt.Attempts == 1,
            _ => true,
        };
    }
    private byte[] ReadBounded(string file)
    {
        using var stream = File.OpenRead(file);
        if (stream.Length > maximumStateBytes)
            throw new InvalidDataException("Broker state exceeds its size limit.");
        using var data = new MemoryStream();
        byte[] buffer = new byte[8192];
        int count;
        while ((count = stream.Read(buffer)) != 0)
        {
            if (data.Length + count > maximumStateBytes) throw new InvalidDataException("Broker state exceeds its size limit.");
            data.Write(buffer, 0, count);
        }
        return data.ToArray();
    }
    private void Commit(State next)
    {
        var temporary = path + ".next";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(stream, next, Json); stream.Flush(true); }
            // Same-directory replacement, matching the repository's AtomicFile primary path. Unlike
            // Move(overwrite), this never asks Windows for a copy-and-delete fallback.
            if (File.Exists(path) && OperatingSystem.IsWindows())
                DesktopAICompanion.ModuleKit.AtomicFile.ReplaceExisting(temporary, path, null!, CancellationToken.None);
            else if (File.Exists(path)) File.Replace(temporary, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else File.Move(temporary, path);
            state = next;
            // Continuations run asynchronously, so completing under gate never runs a waiter here.
            Interlocked.Exchange(ref changeSignal, NewSignal()).TrySetResult();
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { /* Preserve the original write failure; next commit replaces the scratch file. */ }
            catch (UnauthorizedAccessException) { }
        }
    }
    private void EnsureOpen() => ObjectDisposedException.ThrowIf(disposed, this);
    [DoesNotReturn]
    private static void Fail(string code) => throw new BrokerException(code, "Broker request refused: " + code + ".");
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; sessions.Clear(); mailboxActivity.Clear(); ownership.Dispose();
            changeSignal.TrySetResult(); // A waiter wakes and meets the disposed store instead of its deadline.
        }
    }
}
