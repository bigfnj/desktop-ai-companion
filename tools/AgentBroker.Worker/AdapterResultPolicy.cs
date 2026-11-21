namespace AgentBroker.Worker;

// Contain an adapter's malformed result before it reaches the durable store. A journal failure still propagates.
internal static class AdapterResultPolicy
{
    internal static AdapterResult Normalize(AdapterResult? result, out bool invalid)
    {
        bool known = result?.Outcome is AdapterAdmission.Submitted or AdapterAdmission.Delivered or AdapterAdmission.Held or
            AdapterAdmission.Refused or AdapterAdmission.Uncertain or AdapterAdmission.DeferredNotSubmitted;
        if (!known)
        {
            invalid = true;
            return new(AdapterAdmission.Uncertain, "adapter_result_invalid");
        }
        string? reason = result!.Reason;
        invalid = reason is not null && (reason.Length is 0 or > 80 || reason.Any(character =>
            !char.IsAsciiLetterOrDigit(character) && character != '_'));
        return invalid ? result with { Reason = "adapter_reason_invalid" } : result;
    }
}
