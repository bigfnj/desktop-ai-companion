using AgentBroker.Observations;
using AgentBroker.Protocol;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.AgentBroker;

public sealed partial class AgentBrokerModule
{
    private string approvalObservationRaw = "";
    private System.Threading.Timer? approvalExpiry;

    private void AttachApprovalObservation(IHost source)
    {
        source.ContextChanged += ApprovalContextChanged;
        RefreshApprovalObservation(); // A retained publication before this module loaded must not be missed.
    }

    private void DetachApprovalObservation(IHost source)
    {
        source.ContextChanged -= ApprovalContextChanged;
        approvalExpiry?.Dispose(); approvalExpiry = null;
    }

    private void ApprovalContextChanged(string key)
    {
        if (!shutDown && key == CodexApprovalObservation.ContextKey) RefreshApprovalObservation();
    }

    private void RefreshApprovalObservation()
    {
        string raw;
        try { raw = host?.ReadContext(CodexApprovalObservation.ContextKey) ?? ""; } catch { raw = ""; }
        Volatile.Write(ref approvalObservationRaw, raw);
        approvalExpiry?.Dispose(); approvalExpiry = null;
        var now = DateTimeOffset.UtcNow;
        var observation = UsableApprovalObservation(raw, now);
        if (observation is not null)
        {
            var remaining = CodexApprovalObservation.FreshFor - (now - observation.ObservedAtUtc);
            if (remaining > TimeSpan.Zero && remaining <= CodexApprovalObservation.FreshFor + TimeSpan.FromSeconds(5))
            {
                // Context lifetime is the module's, not a worker generation: restarting the worker must not lose expiry.
                approvalExpiry = new(_ => ui?.Post(state => { if (!shutDown) NotifyInfoChanged(); }, null),
                    null, remaining, Timeout.InfiniteTimeSpan);
            }
        }
        NotifyInfoChanged();
    }

    private string ApprovalPromptText()
    {
        var view = CodexApprovalObservation.View(CodexApprovalObservation.Parse(Volatile.Read(ref approvalObservationRaw)), DateTimeOffset.UtcNow);
        return view.State == "fresh"
            ? view.VisiblePromptCount + " visible Codex approval prompt" + (view.VisiblePromptCount == 1 ? "" : "s") + "; conversation unknown."
            : view.State == "stale" ? "Unavailable: the last visible-prompt observation is stale; conversation unknown."
            : "Unavailable: AgentFlow has no current visible-prompt observation; conversation unknown.";
    }

    private ControlRequest ApprovalControlRequest(string? raw = null)
    {
        var value = UsableApprovalObservation(raw ?? Volatile.Read(ref approvalObservationRaw), DateTimeOffset.UtcNow);
        return new("approval-observation", ApprovalPromptCount: value?.Count, ApprovalObservedAtUtc: value?.ObservedAtUtc);
    }

    private static CodexApprovalObservation? UsableApprovalObservation(string raw, DateTimeOffset now)
    {
        var value = CodexApprovalObservation.Parse(raw);
        return CodexApprovalObservation.View(value, now).State == "unavailable" ? null : value;
    }

    private static bool NeedsApprovalObservationForwarding(string raw, string lastRaw, Guid? lastBroker, BrokerSnapshot? snapshot)
    {
        var actual = snapshot?.ApprovalPrompts;
        if (raw.Length == 0 && lastRaw.Length == 0 && actual is null) return false;
        if (raw != lastRaw || snapshot?.BrokerId != lastBroker) return true;
        var expected = UsableApprovalObservation(raw, DateTimeOffset.UtcNow);
        // BrokerId survives a worker restart; its new in-memory snapshot must also confirm the handoff.
        if (expected is null) return actual is not null;
        return actual is null || actual.ObservedAtUtc != expected.ObservedAtUtc ||
            (actual.State == "fresh" && actual.VisiblePromptCount != expected.Count);
    }
}
