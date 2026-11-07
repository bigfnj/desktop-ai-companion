using AgentBroker.CodexDesktopAdapter;
using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Adapters;

internal static class CodexIsolationChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        Console.WriteLine("WITNESS: CODEX_ISOLATION_EXECUTED");
        foreach (string error in new[] { "transport", "owner_capability_mismatch", "invalid_protocol_identity" })
            await LaterRowFailureAsync(error, check);
        try { await CancelledGateWaitAsync(check); }
        catch (SemaphoreFullException)
        {
            check(false, "CODEX_CANCELLED_GATE_WAIT_OWNS_NO_RELEASE: an unacquired release corrupts the gate, including during cleanup");
        }
        await CancelledBeforeNativeCallAsync(check);
        await PublisherUnavailableAsync(check);
        foreach (bool tolerant in new[] { false, true }) await VerificationExpiryAsync(tolerant, check);
    }

    private static async Task LaterRowFailureAsync(string error, Action<bool, string> check)
    {
        Guid healthy = Guid.NewGuid(), failing = Guid.NewGuid(), broker = Guid.NewGuid();
        var discoveryOwner = new Owner();
        var deliveryOwner = new Owner();
        int preparations = 0, fresh = 0;
        var discovery = Provider(discoveryOwner, () => { }, () => [Row(healthy), Row(failing)]);
        var delivery = new CodexNativeProvider(true, _ => Task.FromResult<ICodexOwnerConnection>(deliveryOwner), () => fresh++, null,
            verifyProfilesForDiscovery: () => preparations++);
        await using var adapter = new CodexNativeAdapter(discovery, true, deliveryProvider: delivery);
        var original = await adapter.DiscoverAsync(broker, default);
        discoveryOwner.Failing = failing; discoveryOwner.Failure = error;
        IReadOnlyList<BrokerSession> pass = [];
        try { pass = await adapter.DiscoverAsync(broker, default); }
        catch (AdapterDiscoveryException) { }
        check(pass.Count == 1 && pass[0].Address == original.Single(row => row.Address.SessionId == "codex:" + healthy).Address &&
            pass[0].Capabilities.HasFlag(SessionCapabilities.WakeIdle),
            "CODEX_ROW_FAILURE_CONTAINED: a later " + error + " does not discard or renew another owner's row");
        check(!((ILocalAgentAdapter)adapter).DiscoveryCensusComplete,
            "CODEX_PARTIAL_CENSUS_REPORTED: a skipped owner makes the successful discovery result a partial census");
        if (error != "transport")
            check(discoveryOwner.Disposals == 0,
                "CODEX_ROW_RESPONSE_KEEPS_CONNECTION: a complete invalid owner answer leaves the router protocol in step");
        var result = await adapter.SubmitAsync(Message(pass[0].Address), default);
        check(result.Outcome == AdapterAdmission.Submitted && deliveryOwner.Submissions == 1,
            "CODEX_ROW_AUTHORITY_SNAPSHOT: a later " + error + " cannot erase the healthy row's independently reverified delivery authority");
        check(preparations == 0 && fresh == 1,
            "CODEX_ONE_FRESH_PROFILE_CHECK: independent delivery performs one fresh profile verdict at submission, with no preliminary duplicate");
        var next = await adapter.DiscoverAsync(broker, default);
        check(next.Count == 1 && next[0].Address == pass[0].Address,
            "CODEX_ROW_FAILURE_REPEAT: a chronically failing owner cannot suppress healthy renewal on following passes");
    }

    private static async Task CancelledBeforeNativeCallAsync(Action<bool, string> check)
    {
        var owner = new Owner();
        using var cancel = new CancellationTokenSource();
        bool cancelDuringVerify = true;
        var provider = Provider(owner, () => { if (cancelDuringVerify) cancel.Cancel(); });
        await using (provider)
        {
            var binding = (await provider.ResolveOwnerAsync(Guid.NewGuid(), default))!;
            DesktopSubmissionResult? result = null;
            try { result = await provider.SubmitAsync(binding, Guid.NewGuid(), "Synthetic cancellation boundary.", cancel.Token); }
            catch (OperationCanceledException) { }
            check(result is { Outcome: DesktopSubmissionOutcome.DeferredNotSubmitted, ErrorCode: "native_not_submitted" } && owner.Submissions == 0,
                "CODEX_PRE_NATIVE_CANCELLATION_DEFERS: cancellation after fresh verification but before the native call is a proven non-submission");
            cancelDuringVerify = false;
            check((await provider.SubmitAsync(binding, Guid.NewGuid(), "Synthetic retry.", default)).Outcome == DesktopSubmissionOutcome.Accepted && owner.Submissions == 1,
                "CODEX_PRE_NATIVE_CANCELLATION_RETRY: a later independent attempt still performs its native submission");
        }
    }

    private static async Task PublisherUnavailableAsync(Action<bool, string> check)
    {
        Guid thread = Guid.NewGuid();
        var owner = new Owner();
        bool unavailable = true;
        var provider = new CodexNativeProvider(true, _ => Task.FromResult<ICodexOwnerConnection>(owner),
            () => throw new DesktopAdapterException("unsupported_running_vscode_profile"),
            _ => Task.FromResult<IReadOnlyList<DesktopDirectorySession>>([Row(thread)]), updateTolerantDelivery: true,
            verifyProfilesByPublisher: () => { if (unavailable) throw new DesktopAdapterException("public_image_trust_unavailable"); });
        await using var adapter = new CodexNativeAdapter(provider, true, updateTolerantDelivery: true);
        Guid broker = Guid.NewGuid();
        check((await adapter.DiscoverAsync(broker, default)).Single().Capabilities == SessionCapabilities.Receive,
            "CODEX_PUBLISHER_LOOKUP_PENDING: an unavailable publisher lookup cannot finally refuse queued messages");
        unavailable = false;
        var ready = (await adapter.DiscoverAsync(broker, default)).Single();
        unavailable = true;
        check((await adapter.SubmitAsync(Message(ready.Address), default)) is
            { Outcome: AdapterAdmission.DeferredNotSubmitted, Reason: "native_verification_unavailable" } && owner.Submissions == 0,
            "CODEX_PUBLISHER_LOOKUP_SUBMISSION_DEFERS: an unavailable fresh publisher lookup proves no native submission");
    }

    private static async Task CancelledGateWaitAsync(Action<bool, string> check)
    {
        Guid thread = Guid.NewGuid();
        var owner = new Owner { Hold = true };
        var discovery = Provider(new Owner(), () => { }, () => [Row(thread)]);
        var delivery = Provider(owner, () => { });
        await using var adapter = new CodexNativeAdapter(discovery, true, deliveryProvider: delivery);
        var address = (await adapter.DiscoverAsync(Guid.NewGuid(), default)).Single().Address;
        var holder = adapter.SubmitAsync(Message(address), default);
        await owner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            AdapterResult? cancelled = null;
            try { cancelled = await adapter.SubmitAsync(Message(address), cancel.Token).WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (OperationCanceledException) { }
            check(cancelled is { Outcome: AdapterAdmission.DeferredNotSubmitted, Reason: "native_delivery_wait_cancelled" } && owner.Submissions == 1,
                "CODEX_CANCELLED_GATE_WAIT_DEFERS: a canceled waiter returns a proven non-submission result without touching the provider");
            var retry = adapter.SubmitAsync(Message(address), default);
            try
            {
                await Task.Delay(100);
                check(!retry.IsCompleted && owner.Submissions == 1,
                    "CODEX_CANCELLED_GATE_WAIT_OWNS_NO_RELEASE: canceling a waiter cannot release the holder's gate");
            }
            finally { owner.Release.TrySetResult(); }
            check((await holder).Outcome == AdapterAdmission.Submitted && (await retry).Outcome == AdapterAdmission.Submitted && owner.Submissions == 2,
                "CODEX_CANCELLED_GATE_WAIT_RETRY: the holder and a subsequent retry still submit exactly once");
        }
        finally { owner.Release.TrySetResult(); }
    }

    private static async Task VerificationExpiryAsync(bool tolerant, Action<bool, string> check)
    {
        Guid thread = Guid.NewGuid();
        string? failure = "vscode_process_observation_expired";
        var owner = new Owner();
        var provider = new CodexNativeProvider(true, _ => Task.FromResult<ICodexOwnerConnection>(owner),
            () => { if (failure is not null) throw new DesktopAdapterException(failure); },
            _ => Task.FromResult<IReadOnlyList<DesktopDirectorySession>>([Row(thread)]), updateTolerantDelivery: tolerant);
        await using var adapter = new CodexNativeAdapter(provider, true, updateTolerantDelivery: tolerant);
        Guid broker = Guid.NewGuid();
        var pending = (await adapter.DiscoverAsync(broker, default)).Single();
        check(pending.Capabilities == SessionCapabilities.Receive,
            "CODEX_EXITED_PROCESS_PENDING: positive evidence of a vanished engine keeps only bounded Receive, mode=" + tolerant);
        failure = null;
        var ready = (await adapter.DiscoverAsync(broker, default)).Single();
        failure = "vscode_process_observation_expired";
        check((await adapter.SubmitAsync(Message(ready.Address), default)) is
            { Outcome: AdapterAdmission.DeferredNotSubmitted, Reason: "native_verification_unavailable" } && owner.Submissions == 0,
            "CODEX_EXITED_PROCESS_SUBMISSION_DEFERS: a process exiting during fresh verification never becomes a final refusal, mode=" + tolerant);
        failure = "vscode_process_identity_unverified";
        check((await adapter.DiscoverAsync(broker, default)).Single().Capabilities == SessionCapabilities.None,
            "CODEX_LIVE_IDENTITY_FAILURE_REFUSES: live unreadable ownership remains a verdict, mode=" + tolerant);
        failure = null;
        var restored = (await adapter.DiscoverAsync(broker, default)).Single();
        check((await adapter.SubmitAsync(Message(restored.Address), default)).Outcome == AdapterAdmission.Submitted,
            "CODEX_EXITED_PROCESS_RECOVERS: a fresh completed check can submit the same exact native incarnation, mode=" + tolerant);
    }

    private static CodexNativeProvider Provider(Owner owner, Action verify, Func<IReadOnlyList<DesktopDirectorySession>>? rows = null)
        => new(true, _ => Task.FromResult<ICodexOwnerConnection>(owner), verify,
            _ => Task.FromResult(rows?.Invoke() ?? Array.Empty<DesktopDirectorySession>()));
    private static DesktopDirectorySession Row(Guid thread) => new(thread, DesktopSessionActivity.Idle, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    private static BrokerMessage Message(SessionAddress recipient) => new(Guid.NewGuid(), Guid.NewGuid(), null,
        new(recipient.BrokerId, "synthetic-isolation", Guid.NewGuid()), recipient, "Synthetic isolation check.", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1));

    private sealed class Owner : ICodexOwnerConnection
    {
        private readonly Guid connection = Guid.NewGuid();
        internal Guid Failing;
        internal string Failure = "transport";
        internal int Submissions;
        internal int Disposals;
        internal bool Hold;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool NativeIdleGuard => true;
        public Task<DesktopOwnerBinding?> DiscoverAsync(Guid conversationId, CancellationToken token)
        {
            if (conversationId == Failing)
            {
                if (Failure == "transport") throw new IOException("Synthetic owner lookup transport failure.");
                throw new DesktopAdapterException(Failure);
            }
            return Task.FromResult<DesktopOwnerBinding?>(new(conversationId, conversationId, connection, 12345, 134000000000000000, true)
                { RouterTrust = DesktopRouterTrust.ExactPinned });
        }
        public async Task<DesktopSubmissionResult> SubmitAsync(DesktopOwnerBinding binding, Guid messageId, string text, CancellationToken token)
        {
            if (binding.Incarnation != connection) throw new InvalidOperationException("Foreign connection binding.");
            Submissions++; Entered.TrySetResult();
            if (Hold) await Release.Task.WaitAsync(token);
            return new(DesktopSubmissionOutcome.Accepted);
        }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
}
