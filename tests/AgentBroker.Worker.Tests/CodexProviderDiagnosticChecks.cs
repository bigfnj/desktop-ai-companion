using AgentBroker.CodexDesktopAdapter;
using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Adapters;

internal static class CodexProviderDiagnosticChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        foreach (var fixture in new[]
        {
            (Code: "unsupported_running_vscode_profile", Version: (string?)null),
            (Code: "running_vscode_image_mismatch", Version: (string?)null),
            (Code: "unsupported_vscode_router", Version: "1.144.0")
        })
        {
            Exception? failure = new DesktopAdapterException(fixture.Code, fixture.Version);
            int verifies = 0, observations = 0;
            var engine = new SyntheticOwner();
            var provider = Create(engine, () => { verifies++; if (failure is not null) throw failure; },
                () => observations++);
            await using var adapter = new CodexNativeAdapter(provider, enableDelivery: true);
            Guid broker = Guid.NewGuid();
            var sessions = await adapter.DiscoverAsync(broker, default);
            var snapshot = adapter.DiscoveryDiagnostics;
            check(sessions.Count == 1 && sessions[0].Capabilities == SessionCapabilities.None && !provider.NativeIdleGuard,
                "CodexProviderDiagnosticChecks.cs: " + fixture.Code + " retains observed sessions while refusing receive and wake capability");
            check(snapshot.Count == 1 && snapshot[0].Code == fixture.Code &&
                (fixture.Version is null ? snapshot[0].Description.Contains("installed version unknown", StringComparison.Ordinal)
                    : snapshot[0].Description.Contains(fixture.Version + " installed", StringComparison.Ordinal)),
                "CodexProviderDiagnosticChecks.cs: " + fixture.Code + " reports its exact reason and only already-known version metadata");
            var refused = await adapter.SubmitAsync(Message(sessions[0]), default);
            check(refused.Outcome == AdapterAdmission.Refused && engine.Submissions == 0,
                "CodexProviderDiagnosticChecks.cs: failed discovery profile cannot reach native submission");
            failure = null;
            var recovered = await adapter.DiscoverAsync(broker, default);
            check(recovered.Count == 1 && recovered[0].Capabilities.HasFlag(SessionCapabilities.Receive | SessionCapabilities.WakeIdle) &&
                provider.NativeIdleGuard && adapter.DiscoveryDiagnostics.Count == 0,
                "CodexProviderDiagnosticChecks.cs: supported profile recovery restores capabilities and clears its warning");
            check(snapshot.Count == 1 && snapshot[0].Code == fixture.Code,
                "CodexProviderDiagnosticChecks.cs: previously published profile diagnostic snapshots remain immutable");
            check(verifies == 2 && observations == 2,
                "CodexProviderDiagnosticChecks.cs: reporting preserves one existing profile check and observation per discovery pass");
        }

        foreach (Exception rejected in new Exception[]
        {
            new IOException("Synthetic private detail must never enter status."),
            new UnauthorizedAccessException("Synthetic private detail must never enter status.")
        })
        {
            Exception? failure = rejected;
            var engine = new SyntheticOwner();
            var provider = Create(engine, () => { if (failure is not null) throw failure; });
            await using var adapter = new CodexNativeAdapter(provider, enableDelivery: true);
            var sessions = await adapter.DiscoverAsync(Guid.NewGuid(), default);
            var diagnostic = adapter.DiscoveryDiagnostics.Single();
            check(sessions.Count == 1 && sessions[0].Capabilities == SessionCapabilities.None &&
                diagnostic.Code == "vscode_profile_unavailable" && !diagnostic.ToStatusText().Contains(rejected.Message, StringComparison.Ordinal),
                "CodexProviderDiagnosticChecks.cs: profile IO or access failure keeps observations and emits only a safe generic reason");
            check((await adapter.SubmitAsync(Message(sessions[0]), default)).Outcome == AdapterAdmission.Refused && engine.Submissions == 0,
                "CodexProviderDiagnosticChecks.cs: profile IO or access failure cannot reach native submission");
            failure = null;
            await adapter.DiscoverAsync(Guid.NewGuid(), default);
            check(adapter.DiscoveryDiagnostics.Count == 0,
                "CodexProviderDiagnosticChecks.cs: healthy profile verification clears generic IO or access diagnostics");
        }

        int disabledVerifies = 0;
        var disabledEngine = new SyntheticOwner();
        var disabledProvider = Create(disabledEngine, () => { disabledVerifies++; throw new DesktopAdapterException("unsupported_vscode_profile"); },
            enableDelivery: false);
        await using (var disabled = new CodexNativeAdapter(disabledProvider))
        {
            var sessions = await disabled.DiscoverAsync(Guid.NewGuid(), default);
            check(sessions.Count == 1 && sessions[0].Capabilities == SessionCapabilities.None &&
                disabledVerifies == 0 && disabled.DiscoveryDiagnostics.Count == 0,
                "CodexProviderDiagnosticChecks.cs: disabled delivery neither performs its profile check nor invents a profile refusal");
            check((await disabled.SubmitAsync(Message(sessions[0]), default)).Outcome == AdapterAdmission.Refused && disabledEngine.Submissions == 0,
                "CodexProviderDiagnosticChecks.cs: observation-only configuration remains unable to submit native input");
        }

        Exception? changed = null;
        int submissionVerifies = 0;
        var changedEngine = new SyntheticOwner();
        var changedProvider = Create(changedEngine, () => { submissionVerifies++; if (changed is not null) throw changed; });
        await using (var adapter = new CodexNativeAdapter(changedProvider, enableDelivery: true))
        {
            var session = (await adapter.DiscoverAsync(Guid.NewGuid(), default)).Single();
            changed = new DesktopAdapterException("running_vscode_image_mismatch");
            var result = await adapter.SubmitAsync(Message(session), default);
            check(result.Outcome == AdapterAdmission.Refused && changedEngine.Submissions == 0 &&
                !changedProvider.NativeIdleGuard && submissionVerifies == 2,
                "CodexProviderDiagnosticChecks.cs: fresh direct-submission profile verification still fails closed after a compatible discovery");
            await adapter.DiscoverAsync(Guid.NewGuid(), default);
            check(adapter.DiscoveryDiagnostics.Single().Code == "running_vscode_image_mismatch",
                "CodexProviderDiagnosticChecks.cs: the next discovery reports a profile that became incompatible before submission");
        }
    }

    private static CodexNativeProvider Create(SyntheticOwner engine, Action verify, Action? observed = null, bool enableDelivery = true)
        => new(enableDelivery, _ => Task.FromResult<ICodexOwnerConnection>(engine), verify, _ =>
        {
            observed?.Invoke();
            return Task.FromResult<IReadOnlyList<DesktopDirectorySession>>(
                [new(engine.Binding.ConversationId, DesktopSessionActivity.Unknown, DateTimeOffset.UtcNow, null)]);
        });

    private static BrokerMessage Message(BrokerSession recipient)
        => new(Guid.NewGuid(), Guid.NewGuid(), null, new(recipient.Address.BrokerId, "synthetic-profile-sender", Guid.NewGuid()),
            recipient.Address, "Synthetic profile diagnostic input, never sent to a real client.", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1));

    private sealed class SyntheticOwner : ICodexOwnerConnection
    {
        internal DesktopOwnerBinding Binding { get; } = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 12345, 134000000000000000, true);
        internal int Submissions;
        public bool NativeIdleGuard => true;
        public Task<DesktopOwnerBinding?> DiscoverAsync(Guid conversationId, CancellationToken token)
            => Task.FromResult<DesktopOwnerBinding?>(conversationId == Binding.ConversationId ? Binding : null);
        public Task<DesktopSubmissionResult> SubmitAsync(DesktopOwnerBinding binding, Guid messageId, string text, CancellationToken token)
        { Submissions++; return Task.FromResult(new DesktopSubmissionResult(DesktopSubmissionOutcome.Accepted)); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
