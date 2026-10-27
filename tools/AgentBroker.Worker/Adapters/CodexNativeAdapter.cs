using AgentBroker.Core;
using AgentBroker.CodexDesktopAdapter;
using System.Text;

namespace AgentBroker.Worker.Adapters;

/// <summary>
/// The provider's delivery verdict for one owner binding. <see cref="ObservationOnly"/> is a publisher-signed
/// build under strict pins; <see cref="UnsupportedCapability"/> is the opt-in update-tolerant policy finding an
/// unmet publisher, protocol or capability check. Only the two admitting values permit model input.
/// </summary>
public enum CodexDeliveryAuthority { None, ExactPinned, UpdateTolerant, ObservationOnly, UnsupportedCapability }

public interface ICodexNativeProvider : IAsyncDisposable
{
    bool NativeIdleGuard { get; }
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
    private bool disposed;
    public string Id => "codex-native";
    public IReadOnlyList<AdapterDiscoveryDiagnostic> DiscoveryDiagnostics => provider.DiscoveryDiagnostics;

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
            foreach (var row in rows.DistinctBy(r => r.ConversationId))
            {
                token.ThrowIfCancellationRequested();
                if (row.ConversationId == Guid.Empty || row.LastSeenAt > clock.GetUtcNow().AddSeconds(5) ||
                    row.LastSeenAt <= clock.GetUtcNow().AddMinutes(-2)) continue;
                var binding = await provider.ResolveOwnerAsync(row.ConversationId, token);
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
            bindings.Clear(); foreach (var pair in found) bindings.Add(pair.Key, pair.Value);
            return sessions;
        }
        catch (DesktopAdapterException error)
        {
            bindings.Clear();
            throw new AdapterDiscoveryException(DescribeDiscoveryFailure(error), error);
        }
        finally { gate.Release(); }
    }

    public async Task<AdapterResult> SubmitAsync(BrokerMessage message, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            // Under update tolerance an unmet guard reaches the binding check below and refuses with its reason.
            if (!enableDelivery || !provider.NativeIdleGuard && !updateTolerant) return new(AdapterAdmission.Refused, "native_delivery_disabled");
            string formatted = PeerMessageFormatter.Format(message);
            if (Encoding.UTF8.GetByteCount(formatted) > 32 * 1024) return new(AdapterAdmission.Refused, "native_message_too_large");
            if (!bindings.TryGetValue(message.Recipient, out var previous)) return Deferred("native_session_unavailable");
            // A publisher signature permits observation only. Refuse before owner rediscovery, transport or model input.
            if (previous.RouterTrust != DesktopRouterTrust.ExactPinned && !updateTolerant)
                return new(AdapterAdmission.Refused, "codex_observation_only");
            // The opt-in policy refuses an unmet publisher, protocol or capability check just as early.
            if (updateTolerant && Refusal(previous) is { } unmet) return new(AdapterAdmission.Refused, unmet);
            DesktopOwnerBinding? current;
            try { current = await provider.ResolveOwnerAsync(previous.ConversationId, token); }
            catch (Exception ex) when (ex is DesktopAdapterException or IOException or OperationCanceledException)
            { return Deferred("native_owner_unavailable"); } // No model-input method has been called.
            if (!ValidBinding(current, previous.ConversationId) || current!.NativeSessionIncarnation != message.Recipient.Incarnation)
                return Deferred("native_owner_changed");
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
                    DesktopSubmissionOutcome.DeferredNotSubmitted => Deferred("native_context_busy"),
                    DesktopSubmissionOutcome.Rejected => new(AdapterAdmission.Refused, "native_admission_refused"),
                    _ => new(AdapterAdmission.Uncertain, "native_submission_uncertain"),
                };
            }
            catch (DesktopAdapterException ex) when (ex.Code is "message_frame_too_large" or "invalid_message" or "frame_too_large")
            { return new(AdapterAdmission.Refused, "native_message_too_large"); } // Native library proves these before its first model-input write.
            catch (Exception ex) when (ex is DesktopAdapterException or IOException or OperationCanceledException)
            { return new(AdapterAdmission.Uncertain, "native_submission_uncertain"); }
        }
        finally { gate.Release(); }
    }

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
        string description = error.Code switch
        {
            "unsupported_desktop_version" => $"Codex desktop {installed}; verified {DesktopCompatibilityProfile.ApplicationVersion}.",
            "desktop_image_mismatch" => $"Codex desktop {installed}; public image does not match verified {DesktopCompatibilityProfile.ApplicationVersion}.",
            "unsupported_vscode_router" => $"VS Code {installed}; verified router {VsCodeRouterCompatibilityProfile.ProductVersion}.",
            "unsupported_vscode_profile" => $"Codex VS Code {installed}; verified extension {VsCodeCompatibilityProfile.Version} is unavailable.",
            "vscode_image_mismatch" => $"Codex VS Code {installed}; extension public image does not match verified {VsCodeCompatibilityProfile.Version}.",
            "unsupported_running_vscode_profile" => $"Codex VS Code {installed}; running engine does not match verified extension {VsCodeCompatibilityProfile.Version}.",
            "running_vscode_image_mismatch" => $"Codex VS Code {installed}; running engine public image does not match verified extension {VsCodeCompatibilityProfile.Version}.",
            "public_image_signature_untrusted" => $"Codex {installed}; Windows did not verify the public image signature.",
            "public_image_publisher_mismatch" => $"Codex {installed}; the public image is not signed by the expected publisher.",
            "public_image_location_unprotected" => $"Codex desktop {installed}; package files are writable outside the protected app location.",
            "public_image_trust_unavailable" => $"Codex {installed}; public image publisher trust could not be established.",
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
