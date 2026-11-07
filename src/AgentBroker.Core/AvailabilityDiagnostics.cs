using System.Diagnostics.CodeAnalysis;

namespace AgentBroker.Core;

// Facts observed by this store opening, never an assertion that an unobserved session is permanently absent.
public sealed record AvailabilityDiagnostic(string Subject, string Cause, SessionAddress Address);

public sealed partial class BrokerStore
{
    private const int MaximumForgottenAddresses = 512;
    private sealed record ForgottenAddress(SessionAddress Address, string Cause);
    private readonly LinkedList<ForgottenAddress> forgottenAddresses = new();
    private readonly Dictionary<SessionAddress, LinkedListNode<ForgottenAddress>> forgottenByAddress = new();

    /// <summary>Checks exact addressability and captures its refusal subject/cause atomically. Does not renew or route.</summary>
    public void RequireAddressable(SessionAddress address, string subject, string code = "session_unavailable")
    {
        lock (gate)
        {
            EnsureOpen(); address.Validate();
            if (!Addressable(address)) FailAvailability(code, address, subject);
        }
    }

    private void RememberForgotten(SessionAddress address, string cause)
    {
        ClearForgotten(address);
        forgottenByAddress[address] = forgottenAddresses.AddLast(new ForgottenAddress(address, cause));
        while (forgottenAddresses.Count > MaximumForgottenAddresses)
        {
            var oldest = forgottenAddresses.First!;
            forgottenByAddress.Remove(oldest.Value.Address);
            forgottenAddresses.RemoveFirst();
        }
    }

    private void ClearForgotten(SessionAddress address)
    {
        if (forgottenByAddress.Remove(address, out var previous)) forgottenAddresses.Remove(previous);
    }

    private string EndedSessionCause(BrokerSession session) => session.Activity == SessionActivity.Disconnected ? "disconnected" :
        session.Adapter == "mcp" ? "mailbox_retention_elapsed" :
        NativeAdapter(session.Address.SessionId) == session.Adapter ? "native_lease_elapsed" : "session_lease_elapsed";

    private string AvailabilityCause(SessionAddress address)
    {
        if (NativeReplacement(address) is not null) return "native_incarnation_replaced";
        if (sessions.TryGetValue(address, out var row))
        {
            if (row.Activity == SessionActivity.Disconnected || row.LeaseUntil <= Now) return EndedSessionCause(row);
            return !row.Capabilities.HasFlag(SessionCapabilities.Receive) ? "receive_unavailable" : "unknown";
        }
        return forgottenByAddress.TryGetValue(address, out var forgotten) ? forgotten.Value.Cause : "unknown";
    }

    [DoesNotReturn]
    private void FailAvailability(string code, SessionAddress address, string subject) => throw new BrokerException(code,
        "Broker request refused: " + code + ".") { Availability = new(subject, AvailabilityCause(address), address) };
}
