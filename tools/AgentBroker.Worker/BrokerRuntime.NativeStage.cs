using AgentBroker.Core;
using System.Text.Json.Nodes;

namespace AgentBroker.Worker;

// N-broker-native-confirmation-01, stage evidence (AgentBroker 1.10.6, lane N round 2, DECISIONS 127): one report-only
// native_submission_stage line for each settled attempt that a local native adapter (codex-native or claude-native) took.
// DeliverAsync calls WriteNativeStage from Observe, which runs only after TryReleaseUnsubmittedClaim or TryCompleteDelivery
// committed, under the claims gate and before the store gate (the existing order); an attempt whose settlement is rejected
// publishes nothing. A paired broker's recipient, an mcp row and any other adapter never set the native adapter, so they write no
// line. Nothing here routes, retries, settles or admits anything, and no line holds message text, the sender, a label, the
// conversation, replyTo, a native turn ID or the process-local AdmissionEpoch.
public sealed partial class BrokerRuntime
{
    internal const string NativeStageEvent = "native_submission_stage";

    // Keys in order: event, messageId, recipient (the exact three-part address, as advisory_withheld writes it), receiptAttempts (the
    // claim's public schema-1 receipt Attempts, 1 for every attempt, a pre-frame one included), lifetimeOrdinal (this process's count of
    // the message's published attempts, DeliveryObservations.CountAttempt), outcome (after AdapterResultPolicy.Normalize), reason
    // (verbatim; Normalize already bounds it to 80 ASCII characters), stage (NativeStageFamily), elapsedMs (monotonic, on the runtime
    // clock); then eventAt from AppendEvent, and eventsLost only when the sink adds it. A fresh object on every call, which AppendEvent
    // owns. Nothing here throws: the count and the object are in memory, and AppendEvent never throws for a line.
    private void WriteNativeStage(string? adapter, DeliveryClaim claim, AdapterResult result, long elapsedMs)
    {
        var message = claim.Message;
        var recipient = message.Recipient;
        var line = new JsonObject
        {
            ["event"] = NativeStageEvent,
            ["messageId"] = message.Id.ToString("D"),
            ["recipient"] = recipient.BrokerId.ToString("D") + "/" + recipient.SessionId + "/" + recipient.Incarnation.ToString("D"),
            ["receiptAttempts"] = claim.Receipt.Attempts,
            ["lifetimeOrdinal"] = observations.CountAttempt(message, claim.AdmissionEpoch),
            ["outcome"] = result.Outcome.ToString(),
            ["reason"] = result.Reason,
            ["stage"] = NativeStageFamily(adapter, result.Outcome, result.Reason),
            ["elapsedMs"] = elapsedMs,
        };
        store.AppendEvent(line);
    }

    // The line's stage: a cause FAMILY derived from the adapter, the outcome and the exact reason, which the line always carries
    // verbatim. A family is classification, not delivery proof. Precedence: the outcome (submitted, delivered), then owner_changed,
    // pre_frame, answer_wait, connection, and other for everything else (local checks, busy answers, refusals, runtime exceptions and
    // every reason not named here).
    // pre_frame (Codex only) holds just the reasons the Codex adapter gives when no model input was written: the typed rediscovery
    // deferral, its fourth consecutive result settled Uncertain, and an owner lookup that failed before any input method was called.
    // A raised native_uncertain_raised_<code> is NOT pre_frame: CodexNativeAdapter.RaisedReason gives the same reasons for an untyped
    // fault and for a coded library fault that carries no nothing-written promise, and raised_io and raised_cancelled are also the
    // fourth typed pre-frame result's, so the reason alone does not say which side of the turn frame it came from. Such a reason is
    // other, except the owner-change codes above.
    // response_timeout_or_cancelled is a library code that the Codex adapter maps to native_uncertain_timeout before the runtime sees
    // it; it is here for compatibility only. native_submission_uncertain has two Codex producers (a DesktopAdapterException whose code is
    // not a valid reason code, before or after the frame, and an unknown post-frame code), so answer_wait is a judgment for it.
    internal static string NativeStageFamily(string? adapter, AdapterAdmission outcome, string? reason)
    {
        if (outcome == AdapterAdmission.Submitted) return "submitted";
        if (outcome == AdapterAdmission.Delivered) return "delivered";
        if (reason is "native_owner_changed" or "native_uncertain_owner_changed" or "native_uncertain_raised_owner_changed" or
            "native_uncertain_raised_server_incarnation_changed" or "native_uncertain_raised_stale_binding") return "owner_changed";
        bool codex = adapter == "codex-native";
        if (codex && reason is "native_owner_rediscovery_unavailable" or "native_uncertain_rediscovery_timeout" or "native_owner_unavailable")
            return "pre_frame";
        if (reason is "native_uncertain_timeout" or "response_timeout_or_cancelled" ||
            codex && reason is "native_uncertain_native_error" or "native_submission_uncertain") return "answer_wait";
        if (reason is "native_uncertain_connection" or "native_receipt_capacity_reconnect" or "native_transport_unavailable" ||
            adapter == "claude-native" && reason is "native_uncertain_raised_io" or "native_uncertain_raised_access") return "connection";
        return "other";
    }
}
