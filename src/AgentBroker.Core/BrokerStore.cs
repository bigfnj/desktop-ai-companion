using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentBroker.Core;

/// <summary>
/// Single-writer durable broker state. The owner lock belongs to this object, not a thread.
/// Each mutation writes a replacement before publishing in-memory state. No message text is logged.
/// An acknowledged message this broker sent keeps a tombstone without its text (see Tombstoned).
/// </summary>
public sealed class BrokerStore : IDisposable
{
    public sealed class PeerAuthorization
    {
        internal readonly Guid BrokerId, Stamp;
        internal PeerAuthorization(Guid brokerId, Guid stamp) { BrokerId = brokerId; Stamp = stamp; }
    }
    private sealed record State(int Version, Guid BrokerId, List<PeerTrust> Peers, List<MessageReceipt> Messages);
    // N-broker-a-02, owner decision 2026-10-27 ("Tombstone on ack"). Once a message this broker sent is Delivered or
    // Refused, its record leaves broker-state.json for broker-tombstones.jsonl without its text: a retry compares
    // TextSha256, and receipts, replies and waits need only the rest. broker-state.json keeps exactly the schema-1
    // shape a 1.0.4 worker reads (it disallows unmapped members, and its loader refuses more than 4,096 messages or an
    // empty Text), so tombstones live in a file 1.0.4 never opens.
    private sealed record Tombstone(Guid Id, Guid ConversationId, Guid? ReplyTo, SessionAddress Sender, SessionAddress Recipient,
        string TextSha256, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, int RemainingReplies,
        MessageStatus Status, DateTimeOffset UpdatedAt, string? Reason, int Attempts);
    // The journal's first line. A journal written under another broker identity (a replaced broker-state.json) is stale.
    private sealed record TombstoneJournal(int Version, Guid BrokerId);
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    // One record per line, so an interrupted append can only leave a torn LAST line.
    private static readonly JsonSerializerOptions JournalJson = new(Json) { WriteIndented = false };
    // A receipt, and a tombstone alike, stays this long after its message expires: the window for a late reply and for
    // retry de-duplication. Tombstones keep it unchanged, because they are small enough not to need a shorter one.
    private static readonly TimeSpan RecordGrace = TimeSpan.FromDays(1);
    // The broker's own sender for delivery notices (see WithSenderNotices). No session is ever registered under it, and
    // Register and Accept refuse it, so a message from it can only come from this store and nobody can reply to it.
    public const string NoticeSessionId = "agentbroker:notices";
    private static readonly Guid NoticeIncarnation = new("6a1f0c3e-2b7d-4e95-8c4a-d3f1e07b9a52");
    // Notices owed but not yet admitted, by notice ID, each holding its source receipt without text. A cache, never the
    // authority: RebuildOwed derives it from the durable receipts when the store opens (see WithSenderNotices).
    private Dictionary<Guid, MessageReceipt> owed = new();
    // A tombstone line stays far below this: two 160-character session IDs at six escaped bytes a character, plus
    // fixed-size fields. A longer line is corruption, never a record.
    private const int MaximumJournalLineBytes = 4096;
    // Dead journal lines (expired tombstones, a duplicate left by a failed snapshot write) wait for compaction, which
    // runs once they outnumber live lines by this much. Its cost is then spread over at least as many appends.
    private const int JournalCompactionSlack = 4096;
    private readonly object gate = new();
    private readonly string path;
    private readonly string tombstonePath;
    private readonly Dictionary<Guid, Tombstone> tombstones = new();
    private readonly Dictionary<SessionAddress, HashSet<Guid>> tombstonesByParticipant = new();
    private readonly PriorityQueue<Guid, DateTimeOffset> tombstoneExpiry = new();
    private long journalLength;  // Bytes of complete lines. The next append starts here, dropping any torn tail.
    private long journalEntries; // Tombstone lines in the journal, live or dead; the header is not counted.
    private readonly FileStream ownership;
    private readonly BrokerLimits limits;
    private readonly TimeProvider clock;
    private readonly long maximumStateBytes;
    private readonly long maximumJournalBytes;
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
            this.limits.MaximumPendingPerSession <= 0 || this.limits.MaximumReplies < 0 || this.limits.MaximumTombstones <= 0 ||
            this.limits.DefaultMessageLifetimeMinutes is < 1 or > 1440)
            throw new ArgumentOutOfRangeException(nameof(limits), "Broker limits must be positive (reply budget may be zero); default message lifetime must be 1..1440 minutes.");
        try
        {
            maximumStateBytes = checked((long)this.limits.MaximumMessages * (this.limits.MaximumTextBytes * 6L + 4096) + 1024 * 1024);
            // Lines, live or dead, stay under twice the cap plus the compaction slack and one commit's batch. The byte
            // bound allows 4,096 bytes a line, about five times a measured line (756 bytes), so a run of failed
            // compactions does not lock the store out.
            maximumJournalBytes = checked((2L * this.limits.MaximumTombstones + JournalCompactionSlack + this.limits.MaximumMessages + 1) * MaximumJournalLineBytes);
        }
        catch (OverflowException) { throw new ArgumentOutOfRangeException(nameof(limits), "Broker state size limit overflows."); }
        System.IO.Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "broker-state.json");
        tombstonePath = Path.Combine(directory, "broker-tombstones.jsonl");
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
            LoadTombstones();
            foreach (var peer in state.Peers) peerAuthorizations.Add(peer.BrokerId, Guid.NewGuid());
            // Submission interrupted between adapter admission and receipt is ambiguous. Never
            // blindly re-submit a native client prompt after a crash and claim exactly-once delivery.
            var recovered = state.Messages.Select(r => r.Status is MessageStatus.Submitting or MessageStatus.Submitted
                ? r with { Status = MessageStatus.Uncertain, UpdatedAt = Now, Reason = "restart_during_submission" }
                : r).ToList();
            // A state an older worker wrote (1.0.4, or this one during a rollback) still holds acknowledged messages
            // with their text. The commit below moves them to tombstones before anything else can read them.
            bool upgraded = recovered.Any(Tombstoned);
            if (!existed || upgraded || !recovered.SequenceEqual(state.Messages)) Commit(state with { Messages = recovered });
            owed = RebuildOwed();
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
            if (session.Address.SessionId == NoticeSessionId) Fail("reserved_session");
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
            // Notices never pass through here, so nothing a caller or a peer submits can claim to be one.
            if (message.Sender.SessionId == NoticeSessionId || message.Recipient.SessionId == NoticeSessionId) Fail("reserved_session");
            // A submitted message holding a notice's ID could suppress that notice (see NoticeId).
            if (IsNoticeId(message.Id)) Fail("reserved_message_id");
            if (authenticatedBroker != message.Sender.BrokerId) Fail("sender_mismatch");
            if (authenticatedBroker != state.BrokerId && !state.Peers.Any(p => p.BrokerId == authenticatedBroker))
                Fail("unpaired_peer");
            // An authenticated retry can recover its receipt after a recipient disconnect/restart
            // or message expiry. It must not create a new delivery or require a live old incarnation.
            // An acknowledged original is a tombstone: its text is compared by SHA-256, and a match returns the
            // original receipt carrying the caller's (identical) text, so a peer's receipt check still passes.
            var existing = Stored(message.Id, message.Text, out bool sameText);
            if (existing is not null)
            {
                if (!sameText || existing.Message != message) Fail("message_id_collision");
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
                // A tombstoned parent keeps every field a reply is checked against; only its text is gone.
                var parent = Stored(replyId, null, out _)?.Message;
                if (parent is null || parent.ConversationId != message.ConversationId ||
                    parent.Recipient != message.Sender || parent.Sender != message.Recipient ||
                    parent.RemainingReplies <= 0 || message.RemainingReplies != parent.RemainingReplies - 1)
                    Fail("invalid_reply");
            }
            else if (message.RemainingReplies != limits.MaximumReplies) Fail("invalid_reply_budget");
            var retained = Expire(state.Messages);
            // Only messages that still carry their text count here; acknowledged ones are tombstones.
            if (retained.Count >= limits.MaximumMessages) Fail("message_limit");
            // Every message admitted here may become a tombstone, so the tombstone cap bounds both together and an
            // acknowledgement never has to drop a tombstone (which would lose a retry's de-duplication) to fit.
            PruneTombstones();
            if (retained.Count + tombstones.Count >= limits.MaximumTombstones) Fail("message_limit");
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
            var parent = Stored(originalMessageId, null, out _)?.Message;
            if (parent is null) Fail("invalid_reply");
            if (authenticatedBroker != parent.Recipient.BrokerId) Fail("sender_mismatch");
            if (authenticatedBroker != state.BrokerId && !state.Peers.Any(peer => peer.BrokerId == authenticatedBroker))
                Fail("unpaired_peer");
            var existing = Stored(messageId, text, out bool sameText);
            if (existing is not null)
            {
                var reply = existing.Message;
                if (reply.ReplyTo != parent.Id || reply.ConversationId != parent.ConversationId ||
                    reply.Sender != parent.Recipient || reply.Recipient != parent.Sender || !sameText ||
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
            var existing = Stored(messageId, text, out bool sameText);
            if (existing is not null)
            {
                var sent = existing.Message;
                if (sent.Sender != sender || sent.Recipient != recipient || !sameText || sent.ReplyTo is not null ||
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
            var history = state.Messages.Where(r => r.Message.Sender == participant || r.Message.Recipient == participant).ToList();
            // An acknowledged message stays in the history, with its status and routing and an empty Text.
            PruneTombstones();
            if (tombstonesByParticipant.TryGetValue(participant, out var acknowledged))
                history.AddRange(acknowledged.Select(id => Render(tombstones[id], "")));
            return history.ToArray();
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
            // Also commit when an owed notice can now be admitted (its sender came back, or its queue has room); a pass with
            // nothing else to write never writes for a notice that still cannot be admitted.
            if (!messages.SequenceEqual(state.Messages) || owed.Count > 0 && OwedReady(messages)) Commit(state with { Messages = messages });
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
            messages[index] = next; Commit(state with { Messages = messages }); return Stored(messageId, null, out _)!;
        }
    }

    /// <summary>
    /// The receipt of a message this broker holds. An acknowledged message this broker sent is a tombstone and is
    /// answered with its status, routing and timestamps and an empty Text: real message text is never empty or
    /// whitespace, so an empty Text always means the broker no longer has it. For local tools only.
    /// </summary>
    public MessageReceipt? FindReceipt(Guid messageId)
    {
        lock (gate)
        {
            EnsureOpen();
            // A passed deadline reads as Expired at once rather than after the next delivery pass, and owes its notice.
            var messages = Expire(state.Messages);
            if (!messages.SequenceEqual(state.Messages)) Commit(state with { Messages = messages });
            return Stored(messageId, null, out _);
        }
    }

    /// <summary>
    /// Only a receipt that still carries its message text, so a tombstoned ID answers null exactly as an unknown one.
    /// This is what a paired broker is sent. A 1.0.4 peer compares a receipt's whole message, Text included, before it
    /// records the outcome, and would silently ignore an empty Text forever; it already handles "not found".
    /// </summary>
    public MessageReceipt? FindReceiptWithText(Guid messageId)
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
            if (index < 0)
            {
                // Already acknowledged, so a tombstone: the same outcome again is idempotent, and its status is final.
                var acknowledged = Stored(messageId, null, out _);
                if (acknowledged is null || acknowledged.Message.Recipient != recipient) Fail("recipient_mismatch");
                if (acknowledged!.Status == outcome) return acknowledged;
                Fail("receipt_final");
            }
            if (messages[index].Message.Recipient != recipient) Fail("recipient_mismatch");
            var previous = messages[index];
            if (previous.Status == outcome) return previous;
            if (previous.Status is MessageStatus.Delivered or MessageStatus.Refused or MessageStatus.Expired) Fail("receipt_final");
            if (previous.Status == MessageStatus.Accepted && previous.Message.ExpiresAt <= Now) Fail("message_expired");
            var next = previous with { Status = outcome, UpdatedAt = Now, Reason = "recipient_receipt", Attempts = 1 };
            // The answer is what the store now holds: for a Delivered or Refused message this broker sent, a tombstone.
            messages[index] = next; Commit(state with { Messages = messages }); return Stored(messageId, null, out _)!;
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
        .Where(r => r.Message.ExpiresAt > Now - RecordGrace)
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
        (next, var owing) = WithSenderNotices(next);
        // A Delivered or Refused message this broker sent leaves the snapshot as a tombstone. The tombstone is appended
        // durably FIRST: a crash between the two writes leaves the message in both files, the snapshot wins at load,
        // and the worst outcome is an acknowledgement the recipient repeats. The other order could lose both.
        var added = next.Messages.Where(Tombstoned).Select(ToTombstone).ToList();
        if (added.Count > 0)
        {
            AppendTombstones(added);
            next = next with { Messages = next.Messages.Where(receipt => !Tombstoned(receipt)).ToList() };
        }
        var temporary = path + ".next";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(stream, next, Json); stream.Flush(true); }
            ReplaceFile(temporary, path);
            state = next;
            if (owing is not null) owed = owing;
            foreach (var tombstone in added) AddTombstone(tombstone);
            // Continuations run asynchronously, so completing under gate never runs a waiter here.
            Interlocked.Exchange(ref changeSignal, NewSignal()).TrySetResult();
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { /* Preserve the original write failure; next commit replaces the scratch file. */ }
            catch (UnauthorizedAccessException) { }
        }
        if (added.Count > 0 && journalEntries > 2L * tombstones.Count + JournalCompactionSlack) CompactTombstones();
    }

    // Same-directory replacement, matching the repository's AtomicFile primary path. Unlike
    // Move(overwrite), this never asks Windows for a copy-and-delete fallback.
    private static void ReplaceFile(string temporary, string destination)
    {
        if (File.Exists(destination) && OperatingSystem.IsWindows())
            DesktopAICompanion.ModuleKit.AtomicFile.ReplaceExisting(temporary, destination, null!, CancellationToken.None);
        else if (File.Exists(destination)) File.Replace(temporary, destination, destinationBackupFileName: null, ignoreMetadataErrors: true);
        else File.Move(temporary, destination);
    }

    // N-broker-notices-01 (2026-10-28): two messages sent to a mailbox nobody read expired with attempts=0, and their
    // sender was never told. Now a local sender whose message ends Expired or Refused gets one notice in its own queue:
    // a native session is woken with it, and a generic mailbox's wait_for_messages returns it. This runs in Commit, the
    // one write path, so every route to Expired or Refused is covered.
    // A notice is OWED to its sender's exact address while its source receipt is retained and no notice with its ID
    // exists, as a record or a tombstone. The durable receipts are the authority: RebuildOwed derives the owed set when
    // the store opens, and each commit adds what it ends, so neither a restart nor a deferral loses one. A notice is
    // admitted once its sender is addressable and able to receive, with room in its queue and in the store, and gets its
    // timestamps then, never when it became owed. After a restart, expirations commit before discovery re-registers
    // native sessions, so their notices are admitted by the next delivery pass (BeginDelivery). A notice is admitted only
    // while its source is retained (ExpiresAt + RecordGrace, as in Expire); one that cannot be admitted by then is
    // dropped: its sender stayed unavailable, or the store or its queue stayed full, that long. A notice always gets the maximum
    // lifetime, so it outlives its source and an acknowledged one is never recreated. The owed set changes only once the
    // commit's write has succeeded. A notice never begets a notice: its sender can never register.
    private (State Next, Dictionary<Guid, MessageReceipt>? Owed) WithSenderNotices(State next)
    {
        var open = state.Messages.Where(r => r.Status is not (MessageStatus.Delivered or MessageStatus.Refused or MessageStatus.Expired))
            .Select(r => r.Message.Id).ToHashSet();
        Dictionary<Guid, MessageReceipt>? owing = null;
        foreach (var ended in next.Messages.Where(r => r.Status is (MessageStatus.Expired or MessageStatus.Refused) &&
            open.Contains(r.Message.Id) && r.Message.Sender.BrokerId == state.BrokerId && r.Message.Sender.SessionId != NoticeSessionId))
            (owing ??= new(owed)).TryAdd(NoticeId(ended.Message.Id), Source(ended));
        if ((owing ?? owed).Count == 0) return (next, owing);
        owing ??= new(owed);
        // Counts are taken once per pass, so senders that are still away cost one scan and never delay one that is back.
        // Room is what this commit will leave: records keeping their text against MaximumMessages, and every record (those
        // this commit turns into tombstones included) plus the existing tombstones against MaximumTombstones. Counting only
        // the old tombstones let a refusal's own tombstone plus its notice pass the bound and the store fail to reopen.
        // Expired tombstones are pruned lazily (Stored, Accept); without this a delivery pass counted them as room taken, and a
        // notice waited for some unrelated receipt read to prune them (Codex 01a18357, review of e0406c0).
        PruneTombstones();
        var ids = next.Messages.Select(r => r.Message.Id).ToHashSet();
        int withText = next.Messages.Count(r => !Tombstoned(r)), records = next.Messages.Count + tombstones.Count;
        var pending = PendingByRecipient(next.Messages);
        List<MessageReceipt>? messages = null;
        foreach (var (id, source) in owing.OrderBy(p => p.Value.UpdatedAt).ToArray())
        {
            // A notice is sent only while its source is retained, so it never arrives after its evidence has gone.
            if (ids.Contains(id) || tombstones.ContainsKey(id) || source.Message.ExpiresAt + RecordGrace <= Now) { owing.Remove(id); continue; }
            var owner = source.Message.Sender;
            if (!Admissible(owner, withText, records, pending)) continue;
            // Always the maximum lifetime, never the configurable default: a source lives at most that long plus RecordGrace,
            // so the notice's record or tombstone outlives every source it could be rebuilt from, and is never recreated.
            var now = Now;
            (messages ??= next.Messages.ToList()).Add(new(new(id, id, null, new(state.BrokerId, NoticeSessionId, NoticeIncarnation), owner,
                FitText(NoticeText(source)), now, now + BrokerLimits.MaximumLifetime, 0), MessageStatus.Accepted, now));
            withText++; records++;
            pending[owner] = pending.GetValueOrDefault(owner) + 1;
            owing.Remove(id);
        }
        return (messages is null ? next : next with { Messages = messages }, owing);
    }

    // Every terminal message a local session sent (an Expired record, a Refused tombstone) whose notice exists in neither
    // form. On the first start after an upgrade this includes messages that ended without a notice in the retained day.
    private Dictionary<Guid, MessageReceipt> RebuildOwed()
    {
        var ids = state.Messages.Select(r => r.Message.Id).ToHashSet();
        var result = new Dictionary<Guid, MessageReceipt>();
        foreach (var source in state.Messages.Where(r => r.Status is MessageStatus.Expired or MessageStatus.Refused)
            .Concat(tombstones.Values.Where(t => t.Status == MessageStatus.Refused).Select(t => Render(t, ""))))
        {
            if (source.Message.Sender.BrokerId != state.BrokerId || source.Message.Sender.SessionId == NoticeSessionId) continue;
            var id = NoticeId(source.Message.Id);
            if (!ids.Contains(id) && !tombstones.ContainsKey(id)) result.TryAdd(id, Source(source));
        }
        return result;
    }

    // An owed notice needs its source's routing, status, reason, attempts and times, never its text.
    private static MessageReceipt Source(MessageReceipt ended) => ended with { Message = ended.Message with { Text = "" } };

    private Dictionary<SessionAddress, int> PendingByRecipient(List<MessageReceipt> current) => current.Where(r => IsOutstanding(r.Status))
        .GroupBy(r => r.Message.Recipient).ToDictionary(g => g.Key, g => g.Count());

    // The exact sender address must be addressable and able to receive, and the notice must fit every bound an ordinary
    // message must: the store's text and tombstone bounds and the recipient's pending limit.
    private bool Admissible(SessionAddress owner, int withText, int records, Dictionary<SessionAddress, int> pending) =>
        sessions.TryGetValue(owner, out var session) && Addressable(owner) && session.Capabilities.HasFlag(SessionCapabilities.Receive) &&
        withText < limits.MaximumMessages && records < limits.MaximumTombstones &&
        pending.GetValueOrDefault(owner) < limits.MaximumPendingPerSession;

    // Whether a delivery pass should commit to admit an owed notice whose sender has come back or found room.
    private bool OwedReady(List<MessageReceipt> current)
    {
        PruneTombstones();
        int withText = current.Count(r => !Tombstoned(r)), records = current.Count + tombstones.Count;
        var pending = PendingByRecipient(current);
        return owed.Values.Any(source => source.Message.ExpiresAt + RecordGrace > Now && Admissible(source.Message.Sender, withText, records, pending));
    }

    // A notice is held to the text bound every message is, or the snapshot would not load again. At the default 16 KiB a
    // notice is never trimmed; a smaller configured bound keeps its start, which names the message.
    private string FitText(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) <= limits.MaximumTextBytes) return text;
        int length = text.Length;
        while (length > 1 && Encoding.UTF8.GetByteCount(text.AsSpan(0, length)) > limits.MaximumTextBytes) length--;
        if (length > 1 && char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length];
    }

    // One notice per message, whichever commit sees it end: the ID is derived from the message's own. It is version 8 with a
    // fixed 32-bit marker, and Accept refuses exactly that subspace (IsNoticeId), so no submitted message can take a
    // notice's ID. The marker, not version 8 alone, is reserved: the channel bridge derives its own version-8 reply IDs
    // (McpChannelBridge, "agentbroker-channel-reply"), and one carries the marker only with odds of 1 in 2^32.
    private static readonly byte[] NoticeMarker = [0x41, 0x42, 0x4E, 0x31];
    public static Guid NoticeId(Guid messageId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("agentbroker-notice-v1:" + messageId.ToString("D")))[..16];
        NoticeMarker.CopyTo(bytes, 0);
        bytes[7] = (byte)(bytes[7] & 0x0F | 0x80); // version 8 (custom), in Guid's little-endian field layout
        bytes[8] = (byte)(bytes[8] & 0x3F | 0x80); // RFC 4122 variant
        return new Guid(bytes);
    }

    public static bool IsNoticeId(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes);
        return id.Version == 8 && bytes[..4].SequenceEqual(NoticeMarker);
    }

    private string NoticeText(MessageReceipt ended)
    {
        var m = ended.Message;
        string to = sessions.TryGetValue(m.Recipient, out var recipient) && !string.IsNullOrWhiteSpace(recipient.Label)
            ? $"\"{recipient.Label}\" ({m.Recipient.SessionId})" : m.Recipient.SessionId;
        var lifetime = m.ExpiresAt - m.CreatedAt;
        string span = lifetime.TotalMinutes < 120 ? $"{Math.Round(lifetime.TotalMinutes)}-minute" : $"{Math.Round(lifetime.TotalHours)}-hour";
        // Expired means no Delivered receipt, not unseen: a generic recipient can read a message it never acknowledges.
        string outcome = ended.Status == MessageStatus.Expired
            ? $"reached its {span} delivery deadline at {m.ExpiresAt:u} without a Delivered receipt ({ended.Attempts} delivery attempts). " +
              "The broker cannot tell whether the recipient read it. A native claude: or codex: session is woken only while idle, and a " +
              "generic mcp: mailbox is read only while its owner polls; check with the recipient before sending it again."
            : ended.Reason == "recipient_receipt" ? "was refused by its recipient."
            : ended.Reason?.StartsWith("peer_", StringComparison.Ordinal) == true
                ? $"ended at the recipient's paired broker without a Delivered receipt ({ended.Reason})."
                : $"was refused by this broker before delivery ({ended.Reason ?? "refused"}).";
        return $"AgentBroker delivery notice: your message {m.Id} (conversation {m.ConversationId}) to {to}, sent {m.CreatedAt:u}, {outcome}";
    }

    // Which records keep a tombstone instead of their text. Delivered and Refused are final. Held is not: it can still
    // become Delivered, Refused or Expired, a paired sender keeps polling it, and the recipient that held it may read it
    // again, so it keeps its text and counts toward MaximumMessages. Expired was never acknowledged and its recipient
    // can still read it until the record goes, as before. A message FROM a paired broker keeps its text until its
    // record goes too: the sender's broker reconciles by fetching /federation/receipts/{id} and comparing the whole
    // message with its own copy (BrokerRuntime.ReconcilePeersAsync, unchanged since 1.0.4), so a receipt without the
    // text, or a "not found", would leave that sender at Submitted and then Uncertain for good.
    private bool Tombstoned(MessageReceipt receipt) => receipt.Status is MessageStatus.Delivered or MessageStatus.Refused &&
        receipt.Message.Sender.BrokerId == state.BrokerId;

    private static Tombstone ToTombstone(MessageReceipt receipt) => new(receipt.Message.Id, receipt.Message.ConversationId,
        receipt.Message.ReplyTo, receipt.Message.Sender, receipt.Message.Recipient, HashText(receipt.Message.Text),
        receipt.Message.CreatedAt, receipt.Message.ExpiresAt, receipt.Message.RemainingReplies,
        receipt.Status, receipt.UpdatedAt, receipt.Reason, receipt.Attempts);

    private static MessageReceipt Render(Tombstone tombstone, string text) => new(new(tombstone.Id, tombstone.ConversationId,
        tombstone.ReplyTo, tombstone.Sender, tombstone.Recipient, text, tombstone.CreatedAt, tombstone.ExpiresAt,
        tombstone.RemainingReplies), tombstone.Status, tombstone.UpdatedAt, tombstone.Reason, tombstone.Attempts);

    private static string HashText(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // The stored record for an ID as a receipt: the full record, or a tombstone. A tombstone is rendered with
    // candidateText when that text hashes to its SHA-256, so a retry carrying the original text recovers the original
    // receipt exactly, and with an empty Text otherwise. sameText says whether candidateText is the stored text.
    private MessageReceipt? Stored(Guid messageId, string? candidateText, out bool sameText)
    {
        var full = state.Messages.Find(receipt => receipt.Message.Id == messageId);
        if (full is not null) { sameText = full.Message.Text == candidateText; return full; }
        PruneTombstones();
        if (!tombstones.TryGetValue(messageId, out var tombstone)) { sameText = false; return null; }
        sameText = candidateText is not null && HashText(candidateText) == tombstone.TextSha256;
        return Render(tombstone, sameText ? candidateText! : "");
    }

    private void AddTombstone(Tombstone tombstone)
    {
        if (tombstones.Remove(tombstone.Id, out var previous)) Unindex(previous);
        tombstones.Add(tombstone.Id, tombstone);
        foreach (var participant in new[] { tombstone.Sender, tombstone.Recipient })
        {
            if (!tombstonesByParticipant.TryGetValue(participant, out var ids)) tombstonesByParticipant[participant] = ids = new();
            ids.Add(tombstone.Id);
        }
        tombstoneExpiry.Enqueue(tombstone.Id, tombstone.ExpiresAt);
    }

    private void Unindex(Tombstone tombstone)
    {
        foreach (var participant in new[] { tombstone.Sender, tombstone.Recipient })
            if (tombstonesByParticipant.TryGetValue(participant, out var ids) && ids.Remove(tombstone.Id) && ids.Count == 0)
                tombstonesByParticipant.Remove(participant);
    }

    // Retention ends RecordGrace after expiry, exactly when a full record's does (Expire). Pruning is memory only, so a
    // read never rewrites the journal; the dead lines leave at the next compaction.
    private void PruneTombstones()
    {
        var retainedAfter = Now - RecordGrace;
        while (tombstoneExpiry.TryPeek(out var id, out var expires) && expires <= retainedAfter)
        {
            tombstoneExpiry.Dequeue();
            // A queue entry can outlive its tombstone: a duplicate line replaced it at load.
            if (tombstones.TryGetValue(id, out var tombstone) && tombstone.ExpiresAt == expires)
            { tombstones.Remove(id); Unindex(tombstone); }
        }
    }

    private void LoadTombstones()
    {
        if (!File.Exists(tombstonePath)) return;
        using var stream = new FileStream(tombstonePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        if (stream.Length > maximumJournalBytes) throw new InvalidDataException("Broker tombstones exceed their size limit. They were preserved for recovery.");
        var full = state.Messages.Select(receipt => receipt.Message.Id).ToHashSet();
        byte[] buffer = new byte[65536], line = new byte[MaximumJournalLineBytes];
        int used = 0, count;
        long offset = 0, complete = 0, entries = 0;
        bool header = false;
        while ((count = stream.Read(buffer)) != 0)
        {
            for (int index = 0; index < count; index++)
            {
                offset++;
                if (buffer[index] != (byte)'\n')
                {
                    if (used == line.Length) throw MalformedTombstones();
                    line[used++] = buffer[index];
                    continue;
                }
                var record = line.AsSpan(0, used);
                used = 0; complete = offset;
                if (!header)
                {
                    var journal = ParseJournal<TombstoneJournal>(record);
                    if (journal.Version != 1) throw new InvalidDataException("Broker tombstones are incompatible. They were preserved for recovery.");
                    // Another identity's tombstones cannot describe this broker's messages: the next append replaces them.
                    if (journal.BrokerId != state.BrokerId) return;
                    header = true;
                    continue;
                }
                var tombstone = ParseJournal<Tombstone>(record);
                if (!ValidTombstone(tombstone)) throw MalformedTombstones();
                entries++;
                // The snapshot wins over a tombstone for the same ID: that is a crash between the append and the
                // snapshot write, or a rollback worker that admitted the ID again. Either way the full record is current.
                if (!full.Contains(tombstone.Id)) AddTombstone(tombstone);
            }
        }
        // Bytes after the last newline are an interrupted append (or header). They are ignored here and the next
        // append truncates them; nothing before them is affected.
        if (!header) return;
        journalLength = complete;
        journalEntries = entries;
        PruneTombstones();
        if (tombstones.Count > limits.MaximumTombstones) throw new InvalidDataException("Broker tombstones are incompatible or invalid. They were preserved for recovery.");
    }

    private static T ParseJournal<T>(ReadOnlySpan<byte> record)
    {
        try { return JsonSerializer.Deserialize<T>(record, JournalJson) ?? throw MalformedTombstones(); }
        catch (JsonException error) { throw new InvalidDataException("Broker tombstones are malformed. They were preserved for recovery.", error); }
    }

    private static InvalidDataException MalformedTombstones() =>
        new("Broker tombstones are malformed. They were preserved for recovery.");

    private bool ValidTombstone(Tombstone tombstone)
    {
        if (tombstone.Sender is null || tombstone.Recipient is null || tombstone.TextSha256 is null ||
            tombstone.Id == Guid.Empty || tombstone.ConversationId == Guid.Empty || tombstone.Sender == tombstone.Recipient ||
            tombstone.Sender.BrokerId != state.BrokerId || tombstone.TextSha256.Length != 64 || !tombstone.TextSha256.All(Uri.IsHexDigit) ||
            tombstone.ExpiresAt <= tombstone.CreatedAt || tombstone.ExpiresAt - tombstone.CreatedAt > BrokerLimits.MaximumLifetime ||
            tombstone.RemainingReplies < 0 || tombstone.RemainingReplies > limits.MaximumReplies ||
            tombstone.ReplyTo == Guid.Empty || tombstone.ReplyTo == tombstone.Id ||
            tombstone.Status is not (MessageStatus.Delivered or MessageStatus.Refused) || tombstone.Attempts is < 0 or > 1 ||
            tombstone.Status == MessageStatus.Delivered && tombstone.Attempts != 1 ||
            tombstone.Reason?.Length > 80 || tombstone.Reason?.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_') == true) return false;
        try { tombstone.Sender.Validate(); tombstone.Recipient.Validate(); return true; }
        catch (BrokerException) { return false; }
    }

    // One flushed append per commit. It starts at the end of the last complete line, so a torn tail left by an
    // interrupted append is cut off first; a failed append changes nothing the next one does not overwrite.
    private void AppendTombstones(IReadOnlyList<Tombstone> added)
    {
        using var lines = new MemoryStream();
        if (journalLength == 0) WriteJournalLine(lines, new TombstoneJournal(1, state.BrokerId));
        foreach (var tombstone in added) WriteJournalLine(lines, tombstone);
        using (var stream = new FileStream(tombstonePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.SetLength(journalLength);
            stream.Position = journalLength;
            lines.Position = 0;
            lines.CopyTo(stream);
            stream.Flush(true);
        }
        if (journalLength == 0) journalEntries = 0;
        journalLength += lines.Length;
        journalEntries += added.Count;
    }

    private static void WriteJournalLine<T>(Stream stream, T value)
    {
        JsonSerializer.Serialize(stream, value, JournalJson);
        stream.WriteByte((byte)'\n');
    }

    // Rewrites the journal with only the live tombstones, through the same replacement as the snapshot. It runs after
    // a successful commit, so a failure here must not look like a failed commit: the old journal is still complete
    // and correct, only longer, and the next commit tries again.
    private void CompactTombstones()
    {
        PruneTombstones();
        var temporary = tombstonePath + ".next";
        try
        {
            long length;
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
            {
                WriteJournalLine(stream, new TombstoneJournal(1, state.BrokerId));
                foreach (var tombstone in tombstones.Values) WriteJournalLine(stream, tombstone);
                stream.Flush(true);
                length = stream.Length;
            }
            ReplaceFile(temporary, tombstonePath);
            journalLength = length;
            journalEntries = tombstones.Count;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
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
