using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Adapters;
using AgentBroker.CodexDesktopAdapter;
using AgentBroker.ClaudeAdapter;
using System.Text.Json;

int checks = 0;
void Check(bool value, string description) { if (!value) throw new InvalidOperationException(description); checks++; }
var clock = new FixedClock();
Guid broker = Guid.NewGuid(); Guid thread = Guid.NewGuid();
BrokerMessage Message(BrokerSession recipient) => new(Guid.NewGuid(), Guid.NewGuid(), null,
    new(broker, "synthetic-sender", Guid.NewGuid()), recipient.Address, "Synthetic adapter input.", clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(1));

try
{
    var codex = new FakeCodex(thread, clock);
    await using (var observeOnly = new CodexNativeAdapter(codex, clock: clock))
    {
        var row = (await observeOnly.DiscoverAsync(broker, default)).Single();
        Check(row.Capabilities == SessionCapabilities.None && codex.Submits == 0, "Native observation alone grants no wake capability or model input.");
        Check((await observeOnly.SubmitAsync(Message(row), default)).Outcome == AdapterAdmission.Refused && codex.Submits == 0,
            "Default Codex native adapter cannot submit.");
    }
    Check(codex.Disposed, "Adapter disposal closes its injected provider.");

    codex = new FakeCodex(thread, clock);
    await using (var adapter = new CodexNativeAdapter(codex, enableDelivery: true, clock: clock))
    {
        var row = (await adapter.DiscoverAsync(broker, default)).Single();
        Check(row.Activity == SessionActivity.Unknown && row.Capabilities.HasFlag(SessionCapabilities.NativeIdleGuard | SessionCapabilities.Receive | SessionCapabilities.WakeIdle),
            "Only an explicitly enabled typed guard permits unknown native activity admission.");
        Guid stable = row.Address.Incarnation;
        codex.Binding = codex.Binding with { Incarnation = Guid.NewGuid() };
        var again = (await adapter.DiscoverAsync(broker, default)).Single();
        Check(again.Address.Incarnation == stable, "Ephemeral adapter binding changes do not replace a stable native owner address.");
        var message = Message(again);
        codex.Outcome = new(DesktopSubmissionOutcome.DeferredNotSubmitted);
        Check((await adapter.SubmitAsync(message, default)).Outcome == AdapterAdmission.DeferredNotSubmitted && codex.LastMessageId == message.Id,
            "The exact typed native busy guard defers with the original stable message UUID.");
        codex.Outcome = new(DesktopSubmissionOutcome.Uncertain);
        Check((await adapter.SubmitAsync(Message(again), default)).Outcome == AdapterAdmission.Uncertain,
            "Generic native uncertainty never becomes safe retry.");
        codex.Outcome = new(DesktopSubmissionOutcome.Accepted, TurnId: "synthetic-turn");
        Check((await adapter.SubmitAsync(Message(again), default)).Outcome == AdapterAdmission.Submitted,
            "Native admission remains separate from explicit recipient delivery receipt.");
        int beforeLarge = codex.Submits;
        Check((await adapter.SubmitAsync(Message(again) with { Text = new string('\u0001', 16 * 1024) }, default)).Outcome == AdapterAdmission.Refused && codex.Submits == beforeLarge,
            "Oversized escaped native Codex envelope is refused before model input without truncation.");
        codex.TooLarge = true;
        Check((await adapter.SubmitAsync(Message(again), default)).Outcome == AdapterAdmission.Refused,
            "Known native preflight frame-size failure remains a deterministic refusal.");
        codex.TooLarge = false;
        codex.FailSubmit = true;
        Check((await adapter.SubmitAsync(Message(again), default)).Outcome == AdapterAdmission.Uncertain,
            "An exception after entering native submit is conservatively uncertain.");
        codex.FailSubmit = false; int submittedBefore = codex.Submits;
        codex.Binding = codex.Binding with { OwnerClientId = Guid.NewGuid() };
        Check((await adapter.SubmitAsync(Message(again), default)).Outcome == AdapterAdmission.DeferredNotSubmitted && codex.Submits == submittedBefore,
            "A changed native owner never receives a message addressed to the previous incarnation.");
        Check((await adapter.DiscoverAsync(broker, default)).Single().Address.Incarnation != stable,
            "Changing native owner changes the published broker incarnation.");
        codex.Atomic = false;
        Check((await adapter.DiscoverAsync(broker, default)).Single().Capabilities == SessionCapabilities.None,
            "Loss of the verified native app/profile gate removes receive and wake capabilities.");
        codex.Atomic = true; codex.BoundToRecipient = false;
        Check((await adapter.DiscoverAsync(broker, default)).Single().Capabilities == SessionCapabilities.None,
            "A global source/profile gate cannot authorize a widget that is not bound to the exact recipient owner.");
    }

    var owner = Guid.NewGuid();
    var exhausted = new FakeCodexOwnerConnection(new(thread, owner, Guid.NewGuid(), 12345, 134000000000000000, true) { RouterTrust = DesktopRouterTrust.ExactPinned })
        { Failure = "receipt_capacity" };
    var replacement = new FakeCodexOwnerConnection(exhausted.Binding with { Incarnation = Guid.NewGuid() });
    int connections = 0;
    await using (var provider = new CodexNativeProvider(true,
        _ => Task.FromResult<ICodexOwnerConnection>(connections++ == 0 ? exhausted : replacement), () => { },
        _ => Task.FromResult<IReadOnlyList<DesktopDirectorySession>>([])))
    {
        await provider.ObserveAsync(default);
        var binding = await provider.ResolveOwnerAsync(thread, default);
        var id = Guid.NewGuid();
        var capacity = await provider.SubmitAsync(binding!, id, "synthetic request", default);
        Check(capacity.Outcome == DesktopSubmissionOutcome.DeferredNotSubmitted && capacity.ErrorCode == "native_receipt_capacity_reconnect",
            "Exact native receipt capacity is proven unsubmitted and deferred.");
        Check(exhausted.Disposed && !provider.NativeIdleGuard, "Exhausted native connection is retired before retry.");
        var refreshed = await provider.ResolveOwnerAsync(thread, default);
        Check(refreshed!.Incarnation != binding!.Incarnation && refreshed.NativeSessionIncarnation == binding.NativeSessionIncarnation,
            "Receipt-capacity recovery replaces the connection lease while retaining exact native recipient identity.");
        var accepted = await provider.SubmitAsync(refreshed, id, "synthetic request", default);
        Check(accepted.Outcome == DesktopSubmissionOutcome.Accepted && replacement.LastMessageId == id && connections == 2,
            "The unsubmitted message keeps its ID and is admitted once on a new verified connection.");
        replacement.Failure = "other_native_error";
        try { await provider.SubmitAsync(refreshed, Guid.NewGuid(), "other", default); throw new InvalidOperationException("Expected ambiguous native failure."); }
        catch (DesktopAdapterException error)
        { Check(error.Code == "other_native_error" && !replacement.Disposed, "Other native failures are not relabeled safe retry or silently reconnected."); }
    }
    Check(replacement.Disposed, "Replacement native connection closes on provider disposal.");

    var firstDirectory = new FakeCodexDirectoryObserver(thread);
    var newDirectory = new FakeCodexDirectoryObserver(thread);
    int directoryConnections = 0;
    await using (var provider = new CodexNativeProvider(false,
        _ => throw new InvalidOperationException("Read-only observation must not create a delivery owner."), () => { }, null,
        _ => Task.FromResult<ICodexDirectoryObserver>(directoryConnections++ == 0 ? firstDirectory : newDirectory)))
    {
        Check((await provider.ObserveAsync(default)).Single().ConversationId == thread
            && (await provider.ObserveAsync(default)).Single().ConversationId == thread && directoryConnections == 1,
            "Production observer polling retains its verified connection rather than rehashing the native router archive each poll.");
        firstDirectory.Fail = true;
        try { await provider.ObserveAsync(default); throw new InvalidOperationException("Expected failed native observer."); }
        catch (DesktopAdapterException error)
        { Check(error.Code == "directory_observer_disconnected" && firstDirectory.Disposed, "Observer faults retire the failed connection and fail closed."); }
        Check((await provider.ObserveAsync(default)).Single().ConversationId == thread && directoryConnections == 2,
            "The next bounded poll reconnects through the full verified connection factory.");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { await provider.ObserveAsync(canceled.Token); throw new InvalidOperationException("Expected canceled observation."); }
        catch (OperationCanceledException) { Check(directoryConnections == 2, "Canceled polling does not start or replace a verified observer."); }
    }
    Check(newDirectory.Disposed, "Provider disposal closes its persistent metadata observer.");

    int profileVerifications = 0;
    bool profileChanged = false;
    var checkedOwner = new FakeCodexOwnerConnection(new(thread, Guid.NewGuid(), Guid.NewGuid(), 12345, 134000000000000000, true) { RouterTrust = DesktopRouterTrust.ExactPinned });
    await using (var provider = new CodexNativeProvider(true, _ => Task.FromResult<ICodexOwnerConnection>(checkedOwner),
        () => { profileVerifications++; if (profileChanged) throw new DesktopAdapterException("running_vscode_image_mismatch"); },
        _ => Task.FromResult<IReadOnlyList<DesktopDirectorySession>>([new(thread, DesktopSessionActivity.Unknown, clock.GetUtcNow(), null)])))
    {
        await provider.ObserveAsync(default);
        var binding = (await provider.ResolveOwnerAsync(thread, default))!;
        for (int i = 0; i < 20; i++) Check(provider.IsDeliveryAuthorized(binding), "Candidate capabilities use their verified discovery pass.");
        Check(profileVerifications == 1, "A discovery pass performs full profile verification once regardless of candidate capability checks.");
        Check((await provider.SubmitAsync(binding, Guid.NewGuid(), "synthetic", default)).Outcome == DesktopSubmissionOutcome.Accepted
            && profileVerifications == 2 && checkedOwner.SubmissionCount == 1,
            "Actual submission performs a fresh full profile check immediately before entering the owner adapter.");
        await provider.ObserveAsync(default);
        Check(profileVerifications == 3, "A new discovery pass performs its own full profile verification.");
        profileChanged = true;
        var refused = await provider.SubmitAsync(binding, Guid.NewGuid(), "synthetic", default);
        Check(refused.Outcome == DesktopSubmissionOutcome.Rejected && profileVerifications == 4
            && checkedOwner.SubmissionCount == 1 && !provider.NativeIdleGuard,
            "A client profile change between discovery and send removes capability and prevents model input.");
    }

    var profile = EngineProfile.VsCode21304;
    string session = "11111111-2222-4333-8444-555555555555";
    var target = new NativeTarget(@"\\.\pipe\cc-msg-0123456789abcdef0123456789abcdef", 12345,
        @"C:\SyntheticPublic\claude.exe", profile.Sha256, profile.Version, 134000000000000000, session);
    var claude = new FakeClaude(new(target, "idle"));
    await using (var disabled = new ClaudeNativeAdapter(claude, clock: clock))
    {
        var row = (await disabled.DiscoverAsync(broker, default)).Single();
        Check(row.Capabilities == SessionCapabilities.None && claude.Submits == 0 && claude.CredentialReads == 0,
            "Claude discovery uses injected metadata only while credential handling defaults disabled.");
        Check((await disabled.SubmitAsync(Message(row), default)).Outcome == AdapterAdmission.Refused && claude.Submits == 0,
            "Disabled Claude credentials never reach a provider submit boundary.");
    }
    claude = new FakeClaude(new(target, "idle"));
    await using (var enabled = new ClaudeNativeAdapter(claude, enablePeerCredentials: true, clock: clock))
    {
        var row = (await enabled.DiscoverAsync(broker, default)).Single();
        Check(row.Activity == SessionActivity.Idle && row.Capabilities.HasFlag(SessionCapabilities.Receive | SessionCapabilities.WakeIdle) &&
            !row.Capabilities.HasFlag(SessionCapabilities.NativeIdleGuard), "Claude registry idle is a snapshot, never a fabricated native idle guard.");
        var message = Message(row);
        foreach (string? activity in new string?[] { "busy", "waiting", "shell", null, "unknown" })
        {
            claude.Current = claude.Current with { Status = activity };
            var outcome = await enabled.SubmitAsync(message, default);
            Check(outcome.Outcome == AdapterAdmission.DeferredNotSubmitted && claude.Submits == 0 && claude.CredentialReads == 0,
                "Immediate nonidle metadata refresh defers before any credential access.");
        }
        claude.Current = claude.Current with { Status = "idle" };
        var admission = await enabled.SubmitAsync(message, default);
        Check(admission.Outcome == AdapterAdmission.Submitted && claude.Refreshes == 6 && claude.Submits == 1,
            "Immediate idle metadata refresh precedes one unconfirmed native submission.");
        Check(claude.LastMessage is { DelegateNativeAdmission: true, Activity: NativeActivity.Idle, RecipientMode: RecipientMode.Unknown } &&
            claude.LastMessage.MessageId == message.Id && claude.LastMessage.SessionId == session,
            "Claude delegates actual native policy without forging mode, origin or message identity.");
        var escaped = message with { Text = new string('\u0001', 16 * 1024) };
        Check((await enabled.SubmitAsync(escaped, default)).Outcome == AdapterAdmission.Submitted, "Worst-case Core text is kept within the bounded Claude peer-envelope allowance.");
        string formatted = claude.LastMessage!.Text;
        using (var envelope = JsonDocument.Parse(formatted[(formatted.IndexOf('\n') + 1)..]))
            Check(envelope.RootElement.GetProperty("text").GetString() == escaped.Text && envelope.RootElement.GetProperty("messageId").GetGuid() == escaped.Id,
                "Formatted external-agent envelope preserves exact quoted peer content and routing identity.");
        int before = claude.CredentialReads;
        claude.Current = claude.Current with { Target = target with { StartFileTime = target.StartFileTime + 1 } };
        Check((await enabled.SubmitAsync(message, default)).Outcome == AdapterAdmission.DeferredNotSubmitted && claude.CredentialReads == before,
            "PID reuse/native creation change refuses stale incarnation before credentials.");
        Check((await enabled.DiscoverAsync(broker, default)).Single().Address.Incarnation != row.Address.Incarnation,
            "Native Claude creation change publishes a replacement incarnation.");
    }
    Console.WriteLine($"AgentBroker native adapter regression: {checks} assertions passed.");
    return 0;
}
catch (Exception error) { Console.Error.WriteLine(error); return 1; }

sealed class FixedClock : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
}
sealed class FakeCodex(Guid thread, TimeProvider clock) : ICodexNativeProvider
{
    internal DesktopOwnerBinding Binding = new(thread, Guid.NewGuid(), Guid.NewGuid(), 12345, 134000000000000000, true) { RouterTrust = DesktopRouterTrust.ExactPinned };
    internal DesktopSubmissionResult Outcome = new(DesktopSubmissionOutcome.Accepted, TurnId: "synthetic-turn");
    internal bool Atomic = true, BoundToRecipient = true, FailSubmit, TooLarge, Disposed;
    internal int Submits;
    internal Guid LastMessageId;
    public bool NativeIdleGuard => Atomic;
    public bool IsDeliveryAuthorized(DesktopOwnerBinding binding) => Atomic && BoundToRecipient && binding.ConversationId == thread && binding.OwnerClientId == Binding.OwnerClientId;
    public Task<IReadOnlyList<DesktopDirectorySession>> ObserveAsync(CancellationToken token)
        => Task.FromResult<IReadOnlyList<DesktopDirectorySession>>([new(thread, DesktopSessionActivity.Unknown, clock.GetUtcNow(), null)]);
    public Task<DesktopOwnerBinding?> ResolveOwnerAsync(Guid id, CancellationToken token) => Task.FromResult<DesktopOwnerBinding?>(Binding);
    public Task<DesktopSubmissionResult> SubmitAsync(DesktopOwnerBinding binding, Guid id, string text, CancellationToken token)
    { if (TooLarge) throw new DesktopAdapterException("message_frame_too_large"); Submits++; LastMessageId = id; if (FailSubmit) throw new IOException("Synthetic submit failure."); return Task.FromResult(Outcome); }
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}
sealed class FakeClaude(ClaudeNativeCandidate initial) : IClaudeNativeProvider
{
    internal ClaudeNativeCandidate Current = initial;
    internal int Submits, CredentialReads, Refreshes;
    internal NativeMessage? LastMessage;
    public Task<IReadOnlyList<ClaudeNativeCandidate>> DiscoverAsync(CancellationToken token)
        => Task.FromResult<IReadOnlyList<ClaudeNativeCandidate>>([Current]);
    public Task<ClaudeNativeCandidate?> RefreshAsync(int pid, CancellationToken token)
    { Refreshes++; return Task.FromResult<ClaudeNativeCandidate?>(Current); }
    public Task<SubmissionResult> SubmitAsync(NativeTarget target, NativeMessage message, CancellationToken token)
    { Submits++; CredentialReads++; LastMessage = message; return Task.FromResult(new SubmissionResult(SubmissionStatus.SubmittedUnconfirmed, "synthetic_write")); }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class FakeCodexOwnerConnection(DesktopOwnerBinding binding) : ICodexOwnerConnection
{
    internal DesktopOwnerBinding Binding = binding;
    internal string? Failure;
    internal bool Disposed;
    internal Guid LastMessageId;
    internal int SubmissionCount;
    public bool NativeIdleGuard => !Disposed;
    public Task<DesktopOwnerBinding?> DiscoverAsync(Guid conversationId, CancellationToken token)
        => Task.FromResult<DesktopOwnerBinding?>(Binding);
    public Task<DesktopSubmissionResult> SubmitAsync(DesktopOwnerBinding current, Guid id, string text, CancellationToken token)
    {
        if (Failure is { } code) throw new DesktopAdapterException(code);
        SubmissionCount++;
        LastMessageId = id;
        return Task.FromResult(new DesktopSubmissionResult(DesktopSubmissionOutcome.Accepted, "synthetic-turn"));
    }
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}

sealed class FakeCodexDirectoryObserver(Guid conversationId) : ICodexDirectoryObserver
{
    internal bool Fail, Disposed;
    public IReadOnlyList<DesktopDirectorySession> Snapshot()
    {
        if (Fail) throw new DesktopAdapterException("directory_observer_disconnected");
        return [new(conversationId, DesktopSessionActivity.Unknown, DateTimeOffset.UtcNow, null)];
    }
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}
