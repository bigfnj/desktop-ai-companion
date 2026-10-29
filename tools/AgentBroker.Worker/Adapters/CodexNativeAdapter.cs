using AgentBroker.Core;
using AgentBroker.CodexDesktopAdapter;
using System.Text;

namespace AgentBroker.Worker.Adapters;

/// <summary>
/// The provider's delivery verdict for one owner binding. <see cref="ObservationOnly"/> is a publisher-signed
/// build under strict pins; <see cref="UnsupportedCapability"/> is the opt-in update-tolerant policy finding an
/// unmet publisher, protocol or capability check. <see cref="VerificationUnavailable"/> is the broker's own check that
/// could not complete for a binding that would otherwise qualify (N-agentbroker-refusal-transience-01): a transient state,
/// decided before any turn frame, that defers instead of refusing. Only the two admitting values permit model input.
/// </summary>
public enum CodexDeliveryAuthority { None, ExactPinned, UpdateTolerant, ObservationOnly, UnsupportedCapability, VerificationUnavailable }

public interface ICodexNativeProvider : IAsyncDisposable
{
    bool NativeIdleGuard { get; }
    // True while the broker's own checks could not complete (an engine check failed with an IO or access error, or the
    // owner connection is down after a failed lookup): NativeIdleGuard is false for a reason a retry can clear.
    bool VerificationPending => false;
    IReadOnlyList<AdapterDiscoveryDiagnostic> DiscoveryDiagnostics => Array.Empty<AdapterDiscoveryDiagnostic>();
    bool IsDeliveryAuthorized(DesktopOwnerBinding binding) => NativeIdleGuard;
    // A provider without the update-tolerant policy can only ever report the reviewed exact path.
    CodexDeliveryAuthority Authorize(DesktopOwnerBinding binding)
        => IsDeliveryAuthorized(binding) ? CodexDeliveryAuthority.ExactPinned : CodexDeliveryAuthority.None;
    Task<IReadOnlyList<DesktopDirectorySession>> ObserveAsync(CancellationToken token);
    Task<DesktopOwnerBinding?> ResolveOwnerAsync(Guid conversationId, CancellationToken token);
    Task<DesktopSubmissionResult> SubmitAsync(DesktopOwnerBinding binding, Guid messageId, string text, CancellationToken token);
}

/// <summary>Exact native owner routing. Directory observation alone grants no wake capability.</summary>
public sealed class CodexNativeAdapter : ILocalAgentAdapter
{
    private readonly ICodexNativeProvider provider;
    private readonly bool enableDelivery;
    private readonly bool updateTolerant;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<SessionAddress, DesktopOwnerBinding> bindings = [];
    // Conversations whose owner lookup failed or ran out of time on the last pass that reached them. They are looked up
    // last on the next pass, so one slow conversation cannot again hold up the renewal of every one after it.
    private readonly HashSet<Guid> slowLookups = [];
    private int skippedLastPass;
    private bool disposed;
    public string Id => "codex-native";
    // A pass that skipped conversations says so on every pass that does (a degraded pass is never silent).
    public IReadOnlyList<AdapterDiscoveryDiagnostic> DiscoveryDiagnostics => skippedLastPass == 0 ? provider.DiscoveryDiagnostics
        : [.. provider.DiscoveryDiagnostics, new("codex_owner_lookup_skipped", skippedLastPass + " Codex conversation" +
            (skippedLastPass == 1 ? " was" : "s were") + " not renewed this pass: the owner lookup did not finish in time or failed.")];

    public CodexNativeAdapter(bool enableDelivery = false, bool updateTolerantDelivery = false)
        : this(new CodexNativeProvider(enableDelivery, updateTolerantDelivery), enableDelivery, updateTolerantDelivery: updateTolerantDelivery) { }

    public CodexNativeAdapter(ICodexNativeProvider provider, bool enableDelivery = false, TimeProvider? clock = null,
        bool updateTolerantDelivery = false)
    {
        this.provider = provider; this.enableDelivery = enableDelivery; this.clock = clock ?? TimeProvider.System;
        // Strict exact pins stay the default; update tolerance is a separate opt-in on top of delivery.
        updateTolerant = enableDelivery && updateTolerantDelivery;
    }

    public async Task<IReadOnlyList<BrokerSession>> DiscoverAsync(Guid brokerId, CancellationToken token)
    {
        if (brokerId == Guid.Empty) throw new BrokerException("invalid_session", "Broker identity is required.");
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var rows = await provider.ObserveAsync(token);
            if (rows.Count > 256) throw new BrokerException("session_limit", "Native directory exceeded its bound.");
            var found = new Dictionary<SessionAddress, DesktopOwnerBinding>();
            var sessions = new List<BrokerSession>();
            // skipped: not renewed this pass; slow: the lookups that themselves timed out or failed (a conversation merely
            // not reached before the deadline is not slow, and keeps its place).
            var skipped = new HashSet<Guid>();
            var slow = new HashSet<Guid>();
            // OrderBy is stable: last pass's slow lookups go last, everything else keeps the directory's order.
            var ordered = rows.DistinctBy(r => r.ConversationId).OrderBy(r => slowLookups.Contains(r.ConversationId)).ToArray();
            foreach (var row in ordered)
            {
                if (row.ConversationId == Guid.Empty || row.LastSeenAt > clock.GetUtcNow().AddSeconds(5) ||
                    row.LastSeenAt <= clock.GetUtcNow().AddMinutes(-2)) continue;
                // Past the pass deadline nothing more is looked up; what was resolved is still returned and renewed.
                if (token.IsCancellationRequested) { skipped.Add(row.ConversationId); continue; }
                DesktopOwnerBinding? binding;
                // One conversation's lookup timing out (a pinned router can spend its whole discovery timeout, which
                // outlasts the pass deadline) or its transport failing used to fail the whole pass, so no conversation was
                // renewed and every Codex row lapsed while its chat was live (N-agentbroker-native-flicker-01). Now that
                // conversation alone is skipped. A DesktopAdapterException still fails the pass below: it is the router's
                // or profile's verdict, not one slow row. The provider has already discarded the owner connection, so
                // the next lookup reconnects.
                try { binding = await provider.ResolveOwnerAsync(row.ConversationId, token); }
                catch (Exception error) when (error is OperationCanceledException or IOException)
                { skipped.Add(row.ConversationId); slow.Add(row.ConversationId); continue; }
                if (!ValidBinding(binding, row.ConversationId)) continue;
                var address = new SessionAddress(brokerId, "codex:" + row.ConversationId.ToString("D"), binding!.NativeSessionIncarnation);
                found.Add(address, binding);
                bool guarded = enableDelivery && provider.NativeIdleGuard && binding.SupportsUntrustedAppInput && provider.IsDeliveryAuthorized(binding) &&
                    Refusal(binding) is null;
                var activity = row.ActivityObservedAt is { } observed && observed <= clock.GetUtcNow().AddSeconds(5) && observed > clock.GetUtcNow().AddMinutes(-2)
                    ? MapActivity(row.Activity) : SessionActivity.Unknown;
                sessions.Add(new(address, row.DisplayLabel, Id, guarded
                    ? SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply | SessionCapabilities.NativeIdleGuard
                    : SessionCapabilities.None, activity, clock.GetUtcNow().AddSeconds(30)));
            }
            // A skipped conversation keeps its earlier binding until its lease lapses, as it did when a failed pass left
            // every binding in place; submission resolves the owner afresh before any input either way.
            foreach (var pair in bindings)
                if (skipped.Contains(pair.Value.ConversationId) && !found.ContainsKey(pair.Key)) found.Add(pair.Key, pair.Value);
            bindings.Clear(); foreach (var pair in found) bindings.Add(pair.Key, pair.Value);
            slowLookups.Clear(); slowLookups.UnionWith(slow);
            skippedLastPass = skipped.Count;
            return sessions;
        }
        catch (DesktopAdapterException error)
        {
            bindings.Clear();
            throw new AdapterDiscoveryException(DescribeDiscoveryFailure(error), error);
        }
        finally { gate.Release(); }
    }

    public Task<AdapterResult> SubmitAsync(BrokerMessage message, CancellationToken token) => SubmitAsync(message, null, token);

    // No FindSessionByProcess: one Codex server process hosts every chat of its window, including chats discovery has not
    // seen or whose observation aged out, so a server process never proves which chat is asking. Claiming the one
    // discovered chat returned another chat's row (Codex 01a1855c's review of 47adf9a, 2026-10-29). A Codex agent finds
    // its row as codex:<CODEX_THREAD_ID>.

    public async Task<AdapterResult> SubmitAsync(BrokerMessage message, PeerSenderInfo? sender, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            // Under update tolerance an unmet guard reaches the binding check below and refuses with its reason. A guard that
            // is down only because the broker's own check could not complete reaches the owner lookup below instead, which
            // reconnects, and then either submits or defers (N-agentbroker-refusal-transience-01).
            if (!enableDelivery || !provider.NativeIdleGuard && !updateTolerant && !provider.VerificationPending)
                return new(AdapterAdmission.Refused, "native_delivery_disabled");
            string formatted = PeerMessageFormatter.Format(message, sender);
            if (Encoding.UTF8.GetByteCount(formatted) > 32 * 1024) return new(AdapterAdmission.Refused, "native_message_too_large");
            if (!bindings.TryGetValue(message.Recipient, out var previous)) return Deferred("native_session_unavailable");
            // A publisher signature permits observation only. Refuse before owner rediscovery, transport or model input.
            if (previous.RouterTrust != DesktopRouterTrust.ExactPinned && !updateTolerant)
                return new(AdapterAdmission.Refused, "codex_observation_only");
            // The opt-in policy refuses an unmet publisher, protocol or capability check just as early. A check the broker
            // could not complete is not a verdict: it goes on to the fresh owner lookup, which reconnects a dropped owner
            // connection, and is judged there. Live case 2026-10-29 10:22: a busy chat was refused unsupported_capability
            // this way, under heavy disk load, where native_context_busy would have deferred it.
            if (updateTolerant && !Pending(previous) && Refusal(previous) is { } unmet) return new(AdapterAdmission.Refused, unmet);
            DesktopOwnerBinding? current;
            try { current = await provider.ResolveOwnerAsync(previous.ConversationId, token); }
            catch (Exception ex) when (ex is DesktopAdapterException or IOException or OperationCanceledException)
            { return Deferred("native_owner_unavailable"); } // No model-input method has been called.
            if (!ValidBinding(current, previous.ConversationId) || current!.NativeSessionIncarnation != message.Recipient.Incarnation)
                return Deferred("native_owner_changed");
            // Still unable to complete its own check: no model-input method has been called, so this defers with its own
            // reason. An owner that does not advertise the native capability is a verdict and stays refused.
            if (!provider.IsDeliveryAuthorized(current) && Pending(current)) return Deferred("native_verification_unavailable");
            if (!provider.IsDeliveryAuthorized(current))
                return new(AdapterAdmission.Refused, updateTolerant ? Refusal(current) ?? "unsupported_capability" : "native_client_profile_unverified");
            if (Refusal(current) is { } changed) return new(AdapterAdmission.Refused, changed);
            // Binding.Incarnation is a connection lease; stable broker identity uses the native tuple.
            bindings[message.Recipient] = current;
            try
            {
                var result = await provider.SubmitAsync(current, message.Id, formatted, token);
                return result.Outcome switch
                {
                    DesktopSubmissionOutcome.Accepted => new(AdapterAdmission.Submitted, "native_admission_unconfirmed"),
                    DesktopSubmissionOutcome.DeferredNotSubmitted => Deferred(result.ErrorCode switch
                    {
                        "owner_unavailable" => "native_owner_unavailable",
                        // The provider's fresh engine check at submission could not complete; nothing was written.
                        "native_verification_unavailable" => "native_verification_unavailable",
                        _ => "native_context_busy",
                    }),
                    DesktopSubmissionOutcome.Rejected => new(AdapterAdmission.Refused, "native_admission_refused"),
                    _ => new(AdapterAdmission.Uncertain, UncertainReason(result.ErrorCode)),
                };
            }
            catch (DesktopAdapterException ex) when (ex.Code is "message_frame_too_large" or "invalid_message" or "frame_too_large")
            { return new(AdapterAdmission.Refused, "native_message_too_large"); } // Native library proves these before its first model-input write.
            catch (Exception ex) when (ex is DesktopAdapterException or IOException or OperationCanceledException)
            { return new(AdapterAdmission.Uncertain, "native_submission_uncertain"); }
        }
        finally { gate.Release(); }
    }

    // The library's typed cause of an uncertain submission, kept in the receipt so the next one can be diagnosed: two
    // messages to a chat parked on a prompt ended with only the generic reason (N-agentbroker-uncertain-parked-01).
    private static string UncertainReason(string? code) => code switch
    {
        "response_timeout_or_cancelled" => "native_uncertain_timeout",
        "native_error_unconfirmed" => "native_uncertain_native_error",
        "owner_changed_after_submission" => "native_uncertain_owner_changed",
        "connection_io_unconfirmed" => "native_uncertain_connection",
        _ => "native_submission_uncertain",
    };

    private bool Pending(DesktopOwnerBinding binding) => provider.Authorize(binding) == CodexDeliveryAuthority.VerificationUnavailable;

    // A publisher-signed owner stays observable without the native capability; an exact one never lacks it.
    private static bool ValidBinding(DesktopOwnerBinding? binding, Guid thread) => binding is not null && binding.ConversationId == thread &&
        binding.OwnerClientId != Guid.Empty && binding.ServerPid > 1 && binding.ServerCreationFileTime > 0 &&
        binding.Incarnation != Guid.Empty && binding.RouterTrust != DesktopRouterTrust.Unverified &&
        (binding.SupportsUntrustedAppInput || binding.RouterTrust == DesktopRouterTrust.PublisherSigned);

    // Null when this adapter admits the binding. Both the provider's verdict and the binding's own router trust
    // must agree, so a provider that reports the exact path cannot lend it to a publisher-signed router.
    private string? Refusal(DesktopOwnerBinding binding) => provider.Authorize(binding) switch
    {
        CodexDeliveryAuthority.ExactPinned when binding.RouterTrust == DesktopRouterTrust.ExactPinned => null,
        CodexDeliveryAuthority.UpdateTolerant when updateTolerant && binding.RouterTrust != DesktopRouterTrust.Unverified => null,
        CodexDeliveryAuthority.ObservationOnly => "codex_observation_only",
        CodexDeliveryAuthority.UnsupportedCapability => "unsupported_capability",
        _ => updateTolerant ? "unsupported_capability" : "native_client_profile_unverified",
    };
    private static SessionActivity MapActivity(DesktopSessionActivity activity) => activity switch
    {
        DesktopSessionActivity.Idle => SessionActivity.Idle,
        DesktopSessionActivity.Busy => SessionActivity.Busy,
        DesktopSessionActivity.ApprovalRequired => SessionActivity.ApprovalRequired,
        DesktopSessionActivity.Interrupted => SessionActivity.Interrupted,
        _ => SessionActivity.Unknown,
    };
    private static AdapterResult Deferred(string reason) => new(AdapterAdmission.DeferredNotSubmitted, reason);
    internal static AdapterDiscoveryDiagnostic DescribeDiscoveryFailure(DesktopAdapterException error)
    {
        string installed = error.InstalledVersion is { } version ? version + " installed" : "installed version unknown";
        // A build newer than every pin is the update case the owner asked the pane to name (lane broker-pins).
        string? newer = error.Code switch
        {
            "unsupported_desktop_version" when DesktopCompatibilityProfile.IsNewerThanEveryVerified(error.InstalledVersion)
                => AdapterDiscoveryDiagnostic.NewerThanVerified("Codex desktop", error.InstalledVersion!, DesktopCompatibilityProfile.VerifiedVersions),
            "unsupported_vscode_router" when VsCodeRouterCompatibilityProfile.IsNewerThanEveryVerified(error.InstalledVersion)
                => AdapterDiscoveryDiagnostic.NewerThanVerified("VS Code", error.InstalledVersion!, VsCodeRouterCompatibilityProfile.VerifiedVersions),
            "unsupported_vscode_profile" or "unsupported_running_vscode_profile" when VsCodeCompatibilityProfile.IsNewerThanEveryVerified(error.InstalledVersion)
                => AdapterDiscoveryDiagnostic.NewerThanVerified("Codex VS Code", error.InstalledVersion!, VsCodeCompatibilityProfile.VerifiedVersions),
            _ => null,
        };
        if (newer is not null) return new(error.Code, newer);
        string description = error.Code switch
        {
            "unsupported_desktop_version" => $"Codex desktop {installed}; verified {DesktopCompatibilityProfile.VerifiedVersions}.",
            "desktop_image_mismatch" => $"Codex desktop {installed}; public image does not match verified {DesktopCompatibilityProfile.VerifiedVersions}.",
            "unsupported_vscode_router" => $"VS Code {installed}; verified router {VsCodeRouterCompatibilityProfile.VerifiedVersions}.",
            "unsupported_vscode_profile" => $"Codex VS Code {installed}; verified extension {VsCodeCompatibilityProfile.VerifiedVersions} is unavailable.",
            "vscode_image_mismatch" => $"Codex VS Code {installed}; extension public image does not match verified {VsCodeCompatibilityProfile.VerifiedVersions}.",
            "unsupported_running_vscode_profile" => $"Codex VS Code {installed}; running engine does not match verified extension {VsCodeCompatibilityProfile.VerifiedVersions}.",
            "running_vscode_image_mismatch" => $"Codex VS Code {installed}; running engine public image does not match verified extension {VsCodeCompatibilityProfile.VerifiedVersions}.",
            "public_image_signature_untrusted" => $"Codex {installed}; Windows did not verify the public image signature.",
            "public_image_publisher_mismatch" => $"Codex {installed}; the public image is not signed by the expected publisher.",
            "public_image_location_unprotected" => $"Codex desktop {installed}; package files are writable outside the protected app location.",
            "public_image_trust_unavailable" => $"Codex {installed}; public image publisher trust could not be established.",
            // Fails closed: not an exact-pin mismatch, so no publisher evidence stands in and nothing is observed or woken.
            "pin_document_invalid" => "AgentBroker's pin document was refused (" + (DesktopCompatibilityProfile.PinDocumentError ?? "unknown reason")
                + "); Codex native discovery and wake are off until an AgentBroker module update replaces it.",
            _ => "Codex native discovery failed" + (error.InstalledVersion is { } known ? "; installed version " + known : "") + "."
        };
        return new(error.Code, description);
    }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try { if (disposed) return; disposed = true; bindings.Clear(); await provider.DisposeAsync(); }
        finally { gate.Release(); }
    }
}
