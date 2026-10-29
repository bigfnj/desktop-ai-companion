using AgentBroker.CodexDesktopAdapter;
using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Adapters;

// Codex observation-only discovery (strict pins, the default) and the opt-in update-tolerant delivery policy,
// at the worker provider/adapter layer. Every label starts with this file's name for the mutation runner.
internal static class CodexTrustPolicyChecks
{
    private const string File = "CodexTrustPolicyChecks.cs: ";

    internal static async Task RunAsync(Action<bool, string> check)
    {
        await StrictObservationAsync(check);
        await RouterDiagnosticsAsync(check);
        await CompatibleUpdatedBuildAsync(check);
        await PublisherRefusalsAsync(check);
        await ChangedProtocolAsync(check);
        await AdmissionOutcomesAsync(check);
        await BusyAndReconnectAsync(check);
        await ProviderCannotLendExactTrustAsync(check);
        await DeliveryLockAsync(check);
        await TransientVerificationAsync(check);
    }

    private static async Task DeliveryLockAsync(Action<bool, string> check)
    {
        // The lock: a delivery to a chat whose owner connection is down must leave the adapter's lock to the discovery pass.
        // A real reconnect re-verifies the router synchronously (seconds under load, no token bounds it); done inside the
        // lock, the waiting pass outlived its deadline and no row was renewed (independent review P2-1). The fake connect
        // takes 3 s and ignores cancellation, as that verification does; the pass deadline is 1 s.
        var lockOwner = new Owner(DesktopRouterTrust.PublisherSigned);
        bool slowConnect = false;
        var passClock = new System.Diagnostics.Stopwatch();
        var observedAt = new List<TimeSpan>();
        var lockProvider = new CodexNativeProvider(true, async _ =>
            {
                if (slowConnect) await Task.Delay(TimeSpan.FromSeconds(3));
                return lockOwner;
            }, () => { },
            _ =>
            {
                observedAt.Add(passClock.Elapsed);
                return Task.FromResult<IReadOnlyList<DesktopDirectorySession>>(
                    [new(lockOwner.Binding.ConversationId, DesktopSessionActivity.Unknown, DateTimeOffset.UtcNow, null)]);
            }, null, true, () => { });
        await using (var adapter = new CodexNativeAdapter(lockProvider, enableDelivery: true, updateTolerantDelivery: true))
        {
            var row = (await adapter.DiscoverAsync(Guid.NewGuid(), default)).Single();
            lockOwner.DiscoverFailure = new IOException("Synthetic owner lookup failure.");
            await adapter.DiscoverAsync(Guid.NewGuid(), default);
            lockOwner.DiscoverFailure = null; slowConnect = true;
            var delivery = adapter.SubmitAsync(Message(row), default);
            await Task.Delay(50);
            observedAt.Clear(); passClock.Restart();
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
            {
                try { await adapter.DiscoverAsync(Guid.NewGuid(), deadline.Token); } catch (OperationCanceledException) { }
            }
            var outcome = await delivery;
            string waited = observedAt.Count == 0 ? "never" : observedAt[0].TotalMilliseconds.ToString("F0") + " ms";
            check(observedAt.Count == 1 && observedAt[0] < TimeSpan.FromMilliseconds(500) && outcome.Reason == "native_verification_unavailable" &&
                lockOwner.Submissions == 0,
                File + "TRANSIENT_DELIVERY_NEVER_HOLDS_THE_LOCK: the discovery pass gets the lock at once while a delivery meets a dropped owner connection (pass started after " + waited + ", " + outcome.Reason + ")");
        }
    }

    // N-agentbroker-refusal-transience-01, half 2. Live case 2026-10-29 10:22: a send to a busy Codex chat under heavy
    // disk load was Refused unsupported_capability, decided before any turn frame. A broker-side check that could not
    // complete is transient and defers (or recovers on the fresh owner lookup); an owner that does not advertise the
    // native capability, and a verified mismatch, stay refused.
    private static async Task TransientVerificationAsync(Action<bool, string> check)
    {
        foreach (bool tolerant in new[] { true, false })
        {
            string mode = tolerant ? "update-tolerant" : "strict";
            var trust = tolerant ? DesktopRouterTrust.PublisherSigned : DesktopRouterTrust.ExactPinned;
            // The engine check cannot complete (IO) at discovery and at submission.
            bool ioFailure = true;
            var owner = new Owner(trust);
            var provider = Provider(owner, updateTolerant: tolerant, verify: () => { if (ioFailure) throw new IOException("Synthetic engine read failure."); });
            await using (var adapter = new CodexNativeAdapter(provider, enableDelivery: true, updateTolerantDelivery: tolerant))
            {
                var row = (await adapter.DiscoverAsync(Guid.NewGuid(), default)).Single();
                var deferred = await adapter.SubmitAsync(Message(row), default);
                check(provider.Authorize(owner.Binding) == CodexDeliveryAuthority.VerificationUnavailable &&
                    deferred is { Outcome: AdapterAdmission.DeferredNotSubmitted, Reason: "native_verification_unavailable" } && owner.Submissions == 0,
                    File + "TRANSIENT_VERIFY_DEFERS: an engine check that cannot complete defers with its own reason, never a final refusal (" + mode + ", " + deferred.Reason + ")");
                ioFailure = false;
                var recovered = (await adapter.DiscoverAsync(Guid.NewGuid(), default)).Single();
                var submitted = await adapter.SubmitAsync(Message(recovered), default);
                check(submitted.Outcome == AdapterAdmission.Submitted && owner.Submissions == 1,
                    File + "TRANSIENT_VERIFY_DEFERS WITNESS: once the check completes the same chat is submitted (" + mode + ")");
                // Discovery verified, then the fresh check at submission cannot complete: nothing is written, it defers.
                ioFailure = true;
                var atSubmit = await adapter.SubmitAsync(Message(recovered), default);
                check(atSubmit is { Outcome: AdapterAdmission.DeferredNotSubmitted, Reason: "native_verification_unavailable" } && owner.Submissions == 1,
                    File + "TRANSIENT_VERIFY_AT_SUBMIT_DEFERS: the fresh check at submission failing with IO defers (" + mode + ", " + atSubmit.Reason + ")");
                ioFailure = false;
            }

            // The owner connection was discarded by a failed lookup: the message defers WITHOUT connecting (the discovery
            // loop owns the reconnect, independent review P2-1), and is submitted once a discovery pass has reconnected.
            var dropped = new Owner(trust);
            int connects = 0;
            var connected = new CodexNativeProvider(true, _ => { connects++; return Task.FromResult<ICodexOwnerConnection>(dropped); }, () => { },
                _ => Task.FromResult<IReadOnlyList<DesktopDirectorySession>>(
                    [new(dropped.Binding.ConversationId, DesktopSessionActivity.Unknown, DateTimeOffset.UtcNow, null)]),
                null, tolerant, () => { });
            await using (var adapter = new CodexNativeAdapter(connected, enableDelivery: true, updateTolerantDelivery: tolerant))
            {
                var row = (await adapter.DiscoverAsync(Guid.NewGuid(), default)).Single();
                dropped.DiscoverFailure = new IOException("Synthetic owner lookup failure.");
                await adapter.DiscoverAsync(Guid.NewGuid(), default);
                dropped.DiscoverFailure = null;
                bool pendingBefore = connected.VerificationPending;
                int connectsBefore = connects;
                var deferred = await adapter.SubmitAsync(Message(row), default);
                check(pendingBefore && deferred is { Outcome: AdapterAdmission.DeferredNotSubmitted, Reason: "native_verification_unavailable" } &&
                    connects == connectsBefore && dropped.Submissions == 0,
                    File + "TRANSIENT_OWNER_CONNECTION_DEFERS: a message arriving while the owner connection is down defers without connecting (" + mode + ", " + deferred.Reason + ")");
                var renewed = (await adapter.DiscoverAsync(Guid.NewGuid(), default)).Single();
                var reconnected = await adapter.SubmitAsync(Message(renewed), default);
                check(connects == connectsBefore + 1 && reconnected.Outcome == AdapterAdmission.Submitted && dropped.Submissions == 1,
                    File + "TRANSIENT_OWNER_CONNECTION_DEFERS WITNESS: the next discovery pass reconnects and the message is then submitted (" + mode + ", " + reconnected.Reason + ")");
            }
        }

        // Verdicts stay refused: an owner without the native capability, with the engine check unable to complete too.
        bool failing = true;
        var noCapability = new Owner(DesktopRouterTrust.PublisherSigned, capability: false);
        var refusing = Provider(noCapability, updateTolerant: true, verify: () => { if (failing) throw new IOException("Synthetic engine read failure."); });
        await using (var adapter = new CodexNativeAdapter(refusing, enableDelivery: true, updateTolerantDelivery: true))
        {
            var row = (await adapter.DiscoverAsync(Guid.NewGuid(), default)).Single();
            var refused = await adapter.SubmitAsync(Message(row), default);
            check(refused is { Outcome: AdapterAdmission.Refused, Reason: "unsupported_capability" } && noCapability.Submissions == 0,
                File + "TRANSIENT_VERIFY_CAPABILITY_REFUSES: an owner that does not advertise the native capability stays refused (" + refused.Reason + ")");
        }
        // A verified mismatch at submission is a verdict: refused, as before.
        bool mismatch = false;
        var verdictOwner = new Owner(DesktopRouterTrust.ExactPinned);
        var verdict = Provider(verdictOwner, updateTolerant: false,
            verify: () => { if (mismatch) throw new DesktopAdapterException("running_vscode_image_mismatch"); });
        await using (var adapter = new CodexNativeAdapter(verdict, enableDelivery: true))
        {
            var row = (await adapter.DiscoverAsync(Guid.NewGuid(), default)).Single();
            mismatch = true;
            var refused = await adapter.SubmitAsync(Message(row), default);
            check(refused is { Outcome: AdapterAdmission.Refused, Reason: "native_admission_refused" } && verdictOwner.Submissions == 0,
                File + "TRANSIENT_VERIFY_VERDICT_REFUSES WITNESS: a verified mismatch at submission stays refused (" + refused.Reason + ")");
        }
    }

    private static async Task StrictObservationAsync(Action<bool, string> check)
    {
        var owner = new Owner(DesktopRouterTrust.PublisherSigned);
        var provider = Provider(owner, updateTolerant: false);
        await using var adapter = new CodexNativeAdapter(provider, enableDelivery: true);
        var row = (await adapter.DiscoverAsync(Guid.NewGuid(), default)).Single();
        check(row.Capabilities == SessionCapabilities.None && provider.Authorize(owner.Binding) == CodexDeliveryAuthority.ObservationOnly,
            File + "strict observation: a publisher-signed Codex session is listed with no receive, wake or reply capability");
        int resolves = owner.Discoveries;
        var refused = await adapter.SubmitAsync(Message(row), default);
        check(refused is { Outcome: AdapterAdmission.Refused, Reason: "codex_observation_only" } &&
            owner.Discoveries == resolves && owner.Submissions == 0,
            File + "strict observation guard: submission refuses before owner rediscovery, transport or model input");

        var noCapability = new Owner(DesktopRouterTrust.PublisherSigned, capability: false);
        await using (var listed = new CodexNativeAdapter(Provider(noCapability, updateTolerant: false), enableDelivery: true))
        {
            var observed = (await listed.DiscoverAsync(Guid.NewGuid(), default)).Single();
            check(observed.Capabilities == SessionCapabilities.None,
                File + "strict observation: an updated owner without the native capability stays visible and inert");
        }

        var disabledOwner = new Owner(DesktopRouterTrust.PublisherSigned);
        await using (var disabled = new CodexNativeAdapter(Provider(disabledOwner, updateTolerant: true, enableDelivery: false), updateTolerantDelivery: true))
        {
            var observed = (await disabled.DiscoverAsync(Guid.NewGuid(), default)).Single();
            var result = await disabled.SubmitAsync(Message(observed), default);
            check(observed.Capabilities == SessionCapabilities.None && result.Reason == "native_delivery_disabled" && disabledOwner.Submissions == 0,
                File + "WITNESS: update tolerance without the wake switch is inert; delivery stays disabled");
        }
    }

    private static async Task RouterDiagnosticsAsync(Action<bool, string> check)
    {
        foreach (bool tolerant in new[] { false, true })
        {
            var owner = new Owner(DesktopRouterTrust.PublisherSigned);
            var directory = new Observer(owner.Binding.ConversationId)
            { ExactPinRefusal = new DesktopAdapterException("unsupported_desktop_version", "26.1021.3705.0") };
            var provider = new CodexNativeProvider(true, _ => Task.FromResult<ICodexOwnerConnection>(owner), () => { }, null,
                _ => Task.FromResult<ICodexDirectoryObserver>(directory), tolerant, () => { });
            await using var adapter = new CodexNativeAdapter(provider, enableDelivery: true, updateTolerantDelivery: tolerant);
            await adapter.DiscoverAsync(Guid.NewGuid(), default);
            var codes = adapter.DiscoveryDiagnostics.Select(item => item.Code).ToArray();
            string mode = tolerant ? "codex_update_tolerant_delivery" : "codex_observation_only";
            check(codes.Contains("unsupported_desktop_version") && codes.Contains(mode) &&
                adapter.DiscoveryDiagnostics.Any(item => item.Description.Contains("26.1021.3705.0 installed", StringComparison.Ordinal)),
                File + "router diagnostics: a publisher-signed router reports its failed exact pin and mode on every pass (" + mode + ")");
            directory.ExactPinRefusal = null;
            await adapter.DiscoverAsync(Guid.NewGuid(), default);
            check(adapter.DiscoveryDiagnostics.Count == 0,
                File + "router diagnostics: an exactly pinned router clears the observation warning");
        }
    }

    private static async Task CompatibleUpdatedBuildAsync(Action<bool, string> check)
    {
        var owner = new Owner(DesktopRouterTrust.PublisherSigned);
        var provider = Provider(owner, updateTolerant: true);
        await using var adapter = new CodexNativeAdapter(provider, enableDelivery: true, updateTolerantDelivery: true);
        var row = (await adapter.DiscoverAsync(Guid.NewGuid(), default)).Single();
        check(row.Capabilities == (SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply | SessionCapabilities.NativeIdleGuard) &&
            provider.Authorize(owner.Binding) == CodexDeliveryAuthority.UpdateTolerant,
            File + "WITNESS: a compatible updated build under the opt-in receives the unchanged delivery capabilities");
        var message = Message(row);
        var submitted = await adapter.SubmitAsync(message, default);
        check(submitted.Outcome == AdapterAdmission.Submitted && owner.Submissions == 1 && owner.LastMessageId == message.Id,
            File + "update-tolerant delivery: one unconfirmed native submission keeps the stable message ID");

        // Running VS Code engines that fail exact pins but pass the publisher check, under an exact router.
        int publisherChecks = 0;
        var exactRouter = new Owner(DesktopRouterTrust.ExactPinned);
        var mixed = Provider(exactRouter, updateTolerant: true,
            verify: () => throw new DesktopAdapterException("running_vscode_image_mismatch"), verifyByPublisher: () => publisherChecks++);
        await using var mixedAdapter = new CodexNativeAdapter(mixed, enableDelivery: true, updateTolerantDelivery: true);
        var mixedRow = (await mixedAdapter.DiscoverAsync(Guid.NewGuid(), default)).Single();
        check(mixedRow.Capabilities.HasFlag(SessionCapabilities.Receive | SessionCapabilities.WakeIdle) &&
            mixed.Authorize(exactRouter.Binding) == CodexDeliveryAuthority.UpdateTolerant && publisherChecks == 1 &&
            mixedAdapter.DiscoveryDiagnostics.Any(item => item.Code == "running_vscode_image_mismatch"),
            File + "update-tolerant profiles: publisher-verified updated engines stand in for exact pins and stay diagnosed");
        check((await mixedAdapter.SubmitAsync(Message(mixedRow), default)).Outcome == AdapterAdmission.Submitted && publisherChecks == 2,
            File + "update-tolerant profiles: actual submission repeats the fresh publisher check before model input");

        var strictRouter = new Owner(DesktopRouterTrust.ExactPinned);
        int strictPublisherChecks = 0;
        var strict = Provider(strictRouter, updateTolerant: false,
            verify: () => throw new DesktopAdapterException("running_vscode_image_mismatch"), verifyByPublisher: () => strictPublisherChecks++);
        await using var strictAdapter = new CodexNativeAdapter(strict, enableDelivery: true);
        var strictRow = (await strictAdapter.DiscoverAsync(Guid.NewGuid(), default)).Single();
        check(strictRow.Capabilities == SessionCapabilities.None && strictPublisherChecks == 0 &&
            (await strictAdapter.SubmitAsync(Message(strictRow), default)).Outcome == AdapterAdmission.Refused && strictRouter.Submissions == 0,
            File + "strict profiles: without the opt-in an updated engine is never publisher-checked and never delivered to");
    }

    private static async Task PublisherRefusalsAsync(Action<bool, string> check)
    {
        foreach (string code in new[] { "public_image_publisher_mismatch", "public_image_signature_untrusted" })
        {
            // Updated running engines whose signature is wrong or invalid: listed, refused with the clear reason.
            var owner = new Owner(DesktopRouterTrust.PublisherSigned);
            var provider = Provider(owner, updateTolerant: true,
                verify: () => throw new DesktopAdapterException("running_vscode_image_mismatch"),
                verifyByPublisher: () => throw new DesktopAdapterException(code));
            await using var adapter = new CodexNativeAdapter(provider, enableDelivery: true, updateTolerantDelivery: true);
            var row = (await adapter.DiscoverAsync(Guid.NewGuid(), default)).Single();
            int resolves = owner.Discoveries;
            var refused = await adapter.SubmitAsync(Message(row), default);
            check(row.Capabilities == SessionCapabilities.None && refused is { Outcome: AdapterAdmission.Refused, Reason: "unsupported_capability" } &&
                owner.Submissions == 0 && owner.Discoveries == resolves && adapter.DiscoveryDiagnostics.Any(item => item.Code == code),
                File + "update-tolerant refusal: a wrong-publisher or invalid engine signature refuses with unsupported_capability (" + code + ")");

            // The router itself failing its publisher check: no observation at all, and nothing reaches the owner.
            var routerOwner = new Owner(DesktopRouterTrust.PublisherSigned);
            var routerProvider = new CodexNativeProvider(true, _ => Task.FromResult<ICodexOwnerConnection>(routerOwner), () => { }, null,
                _ => Task.FromException<ICodexDirectoryObserver>(new DesktopAdapterException(code, "26.1021.3705.0")), true, () => { });
            await using var routerAdapter = new CodexNativeAdapter(routerProvider, enableDelivery: true, updateTolerantDelivery: true);
            try
            {
                await routerAdapter.DiscoverAsync(Guid.NewGuid(), default);
                throw new InvalidOperationException(File + "a router without publisher trust was observed.");
            }
            catch (AdapterDiscoveryException error)
            {
                check(error.Diagnostic.Code == code && routerOwner.Discoveries == 0 && routerOwner.Submissions == 0,
                    File + "router refusal: a wrong-publisher or invalid router signature fails discovery with its code (" + code + ")");
            }
        }
    }

    private static async Task ChangedProtocolAsync(Action<bool, string> check)
    {
        var owner = new Owner(DesktopRouterTrust.PublisherSigned, capability: false);
        var provider = Provider(owner, updateTolerant: true);
        await using var adapter = new CodexNativeAdapter(provider, enableDelivery: true, updateTolerantDelivery: true);
        var row = (await adapter.DiscoverAsync(Guid.NewGuid(), default)).Single();
        var refused = await adapter.SubmitAsync(Message(row), default);
        check(row.Capabilities == SessionCapabilities.None && provider.Authorize(owner.Binding) == CodexDeliveryAuthority.UnsupportedCapability &&
            refused is { Outcome: AdapterAdmission.Refused, Reason: "unsupported_capability" } && owner.Submissions == 0,
            File + "changed protocol: an updated owner that stops advertising the native capability refuses with unsupported_capability");

        owner.Capability = true;
        var restored = (await adapter.DiscoverAsync(Guid.NewGuid(), default)).Single();
        owner.Capability = false;
        var lost = await adapter.SubmitAsync(Message(restored), default);
        check(lost is { Outcome: AdapterAdmission.Refused, Reason: "unsupported_capability" } && owner.Submissions == 0,
            File + "changed protocol: a capability lost between discovery and send refuses on the fresh owner check");
    }

    private static async Task AdmissionOutcomesAsync(Action<bool, string> check)
    {
        foreach (var (native, admission, reason) in new[]
        {
            (DesktopSubmissionOutcome.Rejected, AdapterAdmission.Refused, "native_admission_refused"),
            (DesktopSubmissionOutcome.DeferredNotSubmitted, AdapterAdmission.DeferredNotSubmitted, "native_context_busy"),
            (DesktopSubmissionOutcome.Uncertain, AdapterAdmission.Uncertain, "native_submission_uncertain"),
        })
        {
            var owner = new Owner(DesktopRouterTrust.PublisherSigned) { Outcome = native };
            await using var adapter = new CodexNativeAdapter(Provider(owner, updateTolerant: true), enableDelivery: true, updateTolerantDelivery: true);
            var row = (await adapter.DiscoverAsync(Guid.NewGuid(), default)).Single();
            var result = await adapter.SubmitAsync(Message(row), default);
            check(result.Outcome == admission && result.Reason == reason && owner.Submissions == 1,
                File + "update-tolerant admission: held, refused and uncertain native outcomes keep their existing meaning (" + reason + ")");
        }
    }

    private static async Task BusyAndReconnectAsync(Action<bool, string> check)
    {
        var owner = new Owner(DesktopRouterTrust.PublisherSigned);
        var provider = Provider(owner, updateTolerant: true);
        await using var adapter = new CodexNativeAdapter(provider, enableDelivery: true, updateTolerantDelivery: true);
        var row = (await adapter.DiscoverAsync(Guid.NewGuid(), default)).Single();
        owner.Binding = owner.Binding with { Incarnation = Guid.NewGuid() };
        var reconnected = await adapter.SubmitAsync(Message(row), default);
        check(reconnected.Outcome == AdapterAdmission.Submitted && owner.Submissions == 1,
            File + "update-tolerant reconnect: a new connection lease to the same native owner keeps the stable address");
        owner.Binding = owner.Binding with { OwnerClientId = Guid.NewGuid() };
        var moved = await adapter.SubmitAsync(Message(row), default);
        check(moved.Outcome == AdapterAdmission.Submitted && owner.Submissions == 2,
            File + "update-tolerant reconnect: a changed owning view keeps the stable address and receives the message (codex-thread-v2)");
        owner.Failure = new DesktopAdapterException("connection_closed");
        var fresh = (await adapter.DiscoverAsync(Guid.NewGuid(), default)).Single();
        var unavailable = await adapter.SubmitAsync(Message(fresh), default);
        check(unavailable is { Outcome: AdapterAdmission.Uncertain, Reason: "native_submission_uncertain" } && owner.Submissions == 3,
            File + "update-tolerant reconnect: a transport failure after entering submission stays uncertain and is never replayed");
    }

    private static async Task ProviderCannotLendExactTrustAsync(Action<bool, string> check)
    {
        foreach (bool tolerant in new[] { false, true })
        {
            var lender = new LendingProvider();
            await using var adapter = new CodexNativeAdapter(lender, enableDelivery: true, updateTolerantDelivery: tolerant);
            var row = (await adapter.DiscoverAsync(Guid.NewGuid(), default)).Single();
            var refused = await adapter.SubmitAsync(Message(row), default);
            check(row.Capabilities == SessionCapabilities.None && refused.Outcome == AdapterAdmission.Refused && lender.Submissions == 0,
                File + "adapter observation guard: a provider claiming the exact path cannot lend it to a publisher-signed binding (" + refused.Reason + ")");
        }
    }

    private static CodexNativeProvider Provider(Owner owner, bool updateTolerant, bool enableDelivery = true,
        Action? verify = null, Action? verifyByPublisher = null)
        => new(enableDelivery, _ => Task.FromResult<ICodexOwnerConnection>(owner), verify ?? (() => { }),
            _ => Task.FromResult<IReadOnlyList<DesktopDirectorySession>>(
                [new(owner.Binding.ConversationId, DesktopSessionActivity.Unknown, DateTimeOffset.UtcNow, null)]),
            null, updateTolerant, verifyByPublisher ?? (() => { }));

    private static BrokerMessage Message(BrokerSession recipient)
        => new(Guid.NewGuid(), Guid.NewGuid(), null, new(recipient.Address.BrokerId, "synthetic-trust-sender", Guid.NewGuid()),
            recipient.Address, "Synthetic trust-policy input, never sent to a real client.", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1));

    private sealed class Owner(DesktopRouterTrust trust, bool capability = true) : ICodexOwnerConnection
    {
        private readonly Guid thread = Guid.NewGuid();
        internal bool Capability { get; set; } = capability;
        private DesktopOwnerBinding? binding;
        internal DesktopOwnerBinding Binding
        {
            get => binding ??= new DesktopOwnerBinding(thread, Guid.NewGuid(), Guid.NewGuid(), 12345, 134000000000000000, Capability) { RouterTrust = trust };
            set => binding = value;
        }
        internal DesktopSubmissionOutcome Outcome { get; set; } = DesktopSubmissionOutcome.Accepted;
        internal Exception? Failure { get; set; }
        internal int Submissions, Discoveries;
        internal Guid LastMessageId;
        public bool NativeIdleGuard => true;
        // A lookup failure makes the provider discard this owner connection, as the adapter library's closed protocol does.
        internal Exception? DiscoverFailure { get; set; }
        public Task<DesktopOwnerBinding?> DiscoverAsync(Guid conversationId, CancellationToken token)
        {
            Discoveries++;
            if (DiscoverFailure is { } failure) return Task.FromException<DesktopOwnerBinding?>(failure);
            return Task.FromResult<DesktopOwnerBinding?>(conversationId == thread ? Binding with { SupportsUntrustedAppInput = Capability } : null);
        }
        public Task<DesktopSubmissionResult> SubmitAsync(DesktopOwnerBinding binding, Guid messageId, string text, CancellationToken token)
        {
            Submissions++; LastMessageId = messageId;
            if (Failure is not null) throw Failure;
            return Task.FromResult(new DesktopSubmissionResult(Outcome, Outcome == DesktopSubmissionOutcome.Accepted ? "synthetic-turn" : null));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Observer(Guid thread) : ICodexDirectoryObserver
    {
        public DesktopAdapterException? ExactPinRefusal { get; set; }
        public IReadOnlyList<DesktopDirectorySession> Snapshot() => [new(thread, DesktopSessionActivity.Unknown, DateTimeOffset.UtcNow, null)];
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // A third-party provider whose verdict claims the exact path for a publisher-signed binding.
    private sealed class LendingProvider : ICodexNativeProvider
    {
        private readonly DesktopOwnerBinding binding = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 12345, 134000000000000000, true)
            { RouterTrust = DesktopRouterTrust.PublisherSigned };
        internal int Submissions;
        public bool NativeIdleGuard => true;
        public bool IsDeliveryAuthorized(DesktopOwnerBinding candidate) => true;
        public CodexDeliveryAuthority Authorize(DesktopOwnerBinding candidate) => CodexDeliveryAuthority.ExactPinned;
        public Task<IReadOnlyList<DesktopDirectorySession>> ObserveAsync(CancellationToken token)
            => Task.FromResult<IReadOnlyList<DesktopDirectorySession>>([new(binding.ConversationId, DesktopSessionActivity.Unknown, DateTimeOffset.UtcNow, null)]);
        public Task<DesktopOwnerBinding?> ResolveOwnerAsync(Guid conversationId, CancellationToken token) => Task.FromResult<DesktopOwnerBinding?>(binding);
        public Task<DesktopSubmissionResult> SubmitAsync(DesktopOwnerBinding candidate, Guid messageId, string text, CancellationToken token)
        { Submissions++; return Task.FromResult(new DesktopSubmissionResult(DesktopSubmissionOutcome.Accepted, "synthetic-turn")); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
