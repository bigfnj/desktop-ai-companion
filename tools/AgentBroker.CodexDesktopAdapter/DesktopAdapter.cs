using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace AgentBroker.CodexDesktopAdapter;

/// <summary>
/// Version-pinned connection to an existing GUI owner. Never launches, resumes, or approves a session.
/// Plaintext delivery requires an explicit caller opt-in and is not an untrusted-app provenance adapter.
/// </summary>
public sealed partial class DesktopAdapter : IAsyncDisposable
{
    private readonly IDesktopConnection connection;
    private readonly DesktopAdapterOptions options;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<Guid, (Guid Thread, string Digest, DesktopSubmissionResult Result)> receipts = [];
    private readonly Guid incarnation = Guid.NewGuid();
    private readonly DesktopDirectoryIndex directory = new();
    private Guid clientId;
    private bool disposed;
    private bool protocolClosed;
    private DesktopProtocolDiagnostic? lastSubmissionDiagnostic;
    private const int MaximumReceipts = 4096;
    /// <summary>Native owner checks idle twice before context dispatch. This is not an engine-wide lock.</summary>
    public bool NativeIdleGuard => options.EnableExternalPeerDelivery;
    /// <summary>How this connection's router was verified. Only an exact pin carries the reviewed delivery profile.</summary>
    public DesktopRouterTrust RouterTrust => connection.Trust;
    /// <summary>For a publisher-signed router, the exact-pin refusal it fell back from (diagnostic only).</summary>
    public DesktopAdapterException? ExactPinRefusal => connection.ExactPinRefusal;

    private DesktopAdapter(IDesktopConnection connection, DesktopAdapterOptions options)
    { this.connection = connection; this.options = options; }

    public static async Task<DesktopAdapter> ConnectAsync(DesktopAdapterOptions? options = null,
        CancellationToken token = default)
    {
        options ??= new();
        options.Validate();
        var connection = await WindowsDesktopConnection.ConnectAsync(options.Timeout, options.AllowPublisherSignedRouter,
            options.ObservationOnlyConnection, token);
        return await ConnectForTestAsync(connection, options, token);
    }

    internal static async Task<DesktopAdapter> ConnectForTestAsync(IDesktopConnection connection,
        DesktopAdapterOptions options, CancellationToken token = default)
    {
        options.Validate();
        var adapter = new DesktopAdapter(connection, options);
        try
        {
            var result = await adapter.RequestAsync("initialize", new() { ["clientType"] = "agentbroker-desktop-adapter" }, 0, null, token);
            adapter.clientId = DesktopFrames.Id(result["result"], "clientId");
            return adapter;
        }
        catch { await adapter.DisposeAsync(); throw; }
    }

    public async Task<DesktopOwnerBinding?> DiscoverAsync(Guid conversationId, CancellationToken token = default)
    {
        if (conversationId == Guid.Empty) throw new DesktopAdapterException("invalid_thread_id");
        await gate.WaitAsync(token);
        try { return await DiscoverCoreAsync(conversationId, null, token); }
        finally { gate.Release(); }
    }

    private async Task<DesktopOwnerBinding?> DiscoverCoreAsync(Guid threadId, Guid? targetClientId, CancellationToken token)
    {
        EnsureOpen();
        connection.VerifyStillSameOwner();
        var response = await RequestAsync("thread-owner-discovery", new()
        { ["hostId"] = "local", ["conversationId"] = threadId.ToString("D") }, 1, targetClientId, token);
        if (DesktopFrames.Text(response, "resultType") == "error")
        {
            if (DesktopFrames.Text(response, "error") == "no-client-found") return null;
            throw new DesktopAdapterException("owner_discovery_rejected");
        }
        var owner = DesktopFrames.Id(response, "handledByClientId");
        if (targetClientId is not null && targetClientId != owner)
            throw new DesktopAdapterException("owner_changed");
        bool capability = DesktopFrames.IsTrue(response["result"], "supportsUntrustedAppInput");
        // An exactly pinned owner always advertises the reviewed capability, so its absence stays a protocol fault.
        // A publisher-signed router is an unreviewed build: its owner stays observable without the capability,
        // and the missing capability is the runtime check that refuses update-tolerant delivery.
        if (!capability && connection.Trust != DesktopRouterTrust.PublisherSigned)
            throw new DesktopAdapterException("owner_capability_mismatch");
        return new(threadId, owner, incarnation, connection.Identity.Pid,
            connection.Identity.CreationFileTime, capability) { RouterTrust = connection.Trust };
    }

    public Task<DesktopSubmissionResult> SubmitAuthorizedPlaintextAsync(DesktopOwnerBinding binding,
        Guid messageId, string text, CancellationToken token = default)
    {
        if (!options.EnableAuthorizedPlaintextDelivery) throw new DesktopAdapterException("plaintext_delivery_disabled");
        return SubmitCoreAsync(binding, messageId, text, false, token);
    }

    /// <summary>
    /// Sends clearly labeled, quoted external agent input using the original GUI owner's idle guard.
    /// Busy deferral is provably before input dispatch; ambiguous outcomes must never be replayed.
    /// The guard is scoped to this owner and does not lock independent API or goal input.
    /// </summary>
    public Task<DesktopSubmissionResult> SubmitExternalPeerAsync(DesktopOwnerBinding binding,
        Guid messageId, string text, CancellationToken token = default)
    {
        if (!options.EnableExternalPeerDelivery) throw new DesktopAdapterException("external_peer_delivery_disabled");
        return SubmitCoreAsync(binding, messageId, text, true, token);
    }

    private async Task<DesktopSubmissionResult> SubmitCoreAsync(DesktopOwnerBinding binding,
        Guid messageId, string text, bool externalPeer, CancellationToken token)
    {
        if (messageId == Guid.Empty || string.IsNullOrWhiteSpace(text) || text.Contains('\0')
            || Encoding.UTF8.GetByteCount(text) > options.MaximumMessageBytes)
            throw new DesktopAdapterException("invalid_message");
        JsonObject turnStart = externalPeer
            ? NativeExternalPeerInput.CreateTurnStart(binding.ConversationId, messageId, text)
            : NativeExternalPeerInput.CreateAuthorizedPlaintextTurnStart(binding.ConversationId, messageId, text);
        if (System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(turnStart).Length > DesktopFrames.MaximumFrameBytes - 2048)
            throw new DesktopAdapterException("message_frame_too_large");
        await gate.WaitAsync(token);
        try
        {
            // A publisher signature permits observation only. Refuse before any frame, receipt or rediscovery
            // unless the caller explicitly opted in to update-tolerant delivery for this connection.
            if (connection.Trust != DesktopRouterTrust.ExactPinned &&
                !(connection.Trust == DesktopRouterTrust.PublisherSigned && options.EnableUpdateTolerantDelivery))
                return new(DesktopSubmissionOutcome.Rejected, ErrorCode: "router_observation_only");
            ValidateBinding(binding);
            string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                (externalPeer ? "external_peer" : "plaintext") + "\0" + text)));
            if (receipts.TryGetValue(messageId, out var previous))
            {
                if (previous.Thread != binding.ConversationId || previous.Digest != digest)
                    throw new DesktopAdapterException("message_id_collision");
                return previous.Result;
            }
            EnsureOpen();
            if (receipts.Count >= MaximumReceipts) throw new DesktopAdapterException("receipt_capacity");
            var current = await DiscoverCoreAsync(binding.ConversationId, binding.OwnerClientId, token);
            // No turn (model-input) frame is written and no receipt reserved yet; owner discovery above wrote only a control
            // frame. So an owner missing here proves this call submitted nothing. On the
            // typed external-peer path that defers for a retry: it is usually a view reconnecting, and refusing it lost messages
            // to live chats (N-agentbroker-refusal-transience-01). The plaintext path keeps its refusal, as it never defers, and
            // the no-client-found answer to the start-turn request below stays final on both.
            if (current is null)
                return new(externalPeer ? DesktopSubmissionOutcome.DeferredNotSubmitted : DesktopSubmissionOutcome.Rejected, ErrorCode: "owner_unavailable");
            ValidateBinding(current);
            token.ThrowIfCancellationRequested();
            var uncertain = new DesktopSubmissionResult(DesktopSubmissionOutcome.Uncertain, ErrorCode: "submission_unconfirmed");
            // Reserve before the first write. A partial write or lost response is never retried automatically.
            receipts.Add(messageId, (binding.ConversationId, digest, uncertain));
            lastSubmissionDiagnostic = null;
            DesktopSubmissionResult outcome;
            try
            {
                var response = await RequestAsync("thread-follower-start-turn", new()
                {
                    ["conversationId"] = binding.ConversationId.ToString("D"),
                    // Let the owner select ordinary local/cloud admission; never force the cloud-only override.
                    ["turnStart"] = turnStart
                }, 2, binding.OwnerClientId, token);
                // Vendor exceptions may omit the owner, but an explicitly supplied owner must agree
                // even on errors. Otherwise a forged/misrouted busy error could permit a duplicate send.
                if (response.ContainsKey("handledByClientId") &&
                    DesktopFrames.Id(response, "handledByClientId") != binding.OwnerClientId)
                    outcome = uncertain with { ErrorCode = "owner_changed_after_submission", Diagnostic = lastSubmissionDiagnostic };
                else if (DesktopFrames.Text(response, "resultType") == "error")
                    outcome = externalPeer &&
                        DesktopFrames.Text(response, "error") == "App context must wait until the current turn finishes"
                        ? new(DesktopSubmissionOutcome.DeferredNotSubmitted, ErrorCode: "native_context_busy", Diagnostic: lastSubmissionDiagnostic)
                        : DesktopFrames.Text(response, "error") == "no-client-found"
                        ? new(DesktopSubmissionOutcome.Rejected, ErrorCode: "owner_unavailable", Diagnostic: lastSubmissionDiagnostic)
                        : uncertain with { ErrorCode = "native_error_unconfirmed", Diagnostic = lastSubmissionDiagnostic };
                else if (DesktopFrames.Id(response, "handledByClientId") != binding.OwnerClientId)
                    outcome = uncertain with { ErrorCode = "owner_changed_after_submission", Diagnostic = lastSubmissionDiagnostic };
                else
                {
                    var result = DesktopFrames.Object(DesktopFrames.Object(response, "result"), "result");
                    string? turnId = DesktopFrames.Text(DesktopFrames.Object(result, "turn"), "id");
                    if (string.IsNullOrWhiteSpace(turnId) || turnId.Length > 128)
                        throw new DesktopAdapterException("invalid_submission_result");
                    outcome = new(DesktopSubmissionOutcome.Accepted, turnId,
                        Duplicate: DesktopFrames.Text(result, "admissionOutcome") == "duplicate", Diagnostic: lastSubmissionDiagnostic);
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or DesktopAdapterException)
            {
                outcome = uncertain with
                {
                    ErrorCode = e is DesktopAdapterException known ? known.Code
                        : e is OperationCanceledException ? "response_timeout_or_cancelled" : "connection_io_unconfirmed",
                    Diagnostic = lastSubmissionDiagnostic
                };
            }
            if (outcome.Outcome == DesktopSubmissionOutcome.DeferredNotSubmitted)
                receipts.Remove(messageId); // Only the pinned pre-injection busy error proves a same-ID retry safe.
            else receipts[messageId] = (binding.ConversationId, digest, outcome);
            return outcome;
        }
        finally { gate.Release(); }
    }

    private void ValidateBinding(DesktopOwnerBinding binding)
    {
        if (binding.Incarnation != incarnation || binding.ServerPid != connection.Identity.Pid
            || binding.ServerCreationFileTime != connection.Identity.CreationFileTime
            || binding.ConversationId == Guid.Empty || binding.OwnerClientId == Guid.Empty
            || !binding.SupportsUntrustedAppInput || binding.RouterTrust != connection.Trust)
            throw new DesktopAdapterException("stale_binding");
    }

    private async Task<JsonObject> RequestAsync(string method, JsonObject parameters, int version,
        Guid? target, CancellationToken token)
    {
        EnsureOpen();
        if (method is not ("initialize" or "thread-owner-discovery" or "thread-follower-start-turn"))
            throw new DesktopAdapterException("method_not_allowed");
        TimeSpan requestTimeout = method == "thread-owner-discovery" ? options.DiscoveryTimeout : options.Timeout;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(requestTimeout);
        string requestId = Guid.NewGuid().ToString("D");
        var request = new JsonObject
        {
            ["type"] = "request", ["requestId"] = requestId,
            ["sourceClientId"] = clientId == Guid.Empty ? "initializing-client" : clientId.ToString("D"),
            ["version"] = version, ["method"] = method, ["params"] = parameters,
            ["timeoutMs"] = (int)requestTimeout.TotalMilliseconds
        };
        if (target.HasValue) request["targetClientId"] = target.Value.ToString("D");
        try
        {
            await DesktopFrames.WriteAsync(connection.Stream, request, deadline.Token);
            for (int ignored = 0; ignored < 128; ignored++)
            {
                var frame = await DesktopFrames.ReadAsync(connection.Stream, deadline.Token);
                string? type = DesktopFrames.Text(frame, "type");
                if (type == "broadcast") { directory.Capture(frame, clientId); continue; }
                if (type == "client-discovery-request")
                {
                    string incomingId = DesktopFrames.Id(frame, "requestId").ToString("D");
                    await DesktopFrames.WriteAsync(connection.Stream, new()
                    {
                        ["type"] = "client-discovery-response", ["requestId"] = incomingId,
                        ["response"] = new JsonObject { ["canHandle"] = false }
                    }, deadline.Token);
                    continue;
                }
                if (type != "response" || DesktopFrames.Text(frame, "requestId") != requestId)
                    throw new DesktopAdapterException("unexpected_response");
                if (method == "thread-follower-start-turn")
                    lastSubmissionDiagnostic = DesktopFrames.DescribeSubmissionResponse(frame);
                string? resultType = DesktopFrames.Text(frame, "resultType");
                string? responseMethod = DesktopFrames.Text(frame, "method");
                // Vendor exception responses omit method/handledByClientId. Success responses must identify both.
                if (resultType is not ("success" or "error")
                    || resultType == "success" && responseMethod != method
                    || resultType == "error" && responseMethod is not null && responseMethod != method)
                    throw new DesktopAdapterException("protocol_mismatch");
                connection.VerifyStillSameOwner();
                return frame;
            }
            throw new DesktopAdapterException("unsolicited_frame_limit");
        }
        catch
        {
            protocolClosed = true;
            await connection.DisposeAsync();
            throw;
        }
    }

    private void EnsureOpen()
    {
        if (disposed || protocolClosed) throw new DesktopAdapterException("connection_closed");
    }

    // Used only on the short-lived dedicated directory observer, never the delivery connection.
    internal async Task<IReadOnlyList<DesktopDirectorySession>> CollectDirectoryCandidatesAsync(TimeSpan window, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            EnsureOpen();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(window);
            try
            {
                while (true)
                {
                    var frame = await DesktopFrames.ReadAsync(connection.Stream, deadline.Token);
                    string? type = DesktopFrames.Text(frame, "type");
                    if (type == "broadcast") { directory.Capture(frame, clientId); continue; }
                    if (type == "client-discovery-request")
                    {
                        await DesktopFrames.WriteAsync(connection.Stream, new()
                        {
                            ["type"] = "client-discovery-response", ["requestId"] = DesktopFrames.Id(frame, "requestId").ToString("D"),
                            ["response"] = new JsonObject { ["canHandle"] = false }
                        }, deadline.Token);
                        continue;
                    }
                    throw new DesktopAdapterException("unexpected_observer_frame");
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            finally
            {
                // Timeout may cancel a partial read, so this observer is never reused for delivery.
                protocolClosed = true;
                await connection.DisposeAsync();
            }
            return directory.Snapshot();
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        await connection.DisposeAsync();
        // Do not destroy the semaphore while a canceled caller may be releasing it.
    }
}
