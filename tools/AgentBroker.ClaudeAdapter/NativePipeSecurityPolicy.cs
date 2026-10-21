using System.Security.AccessControl;
using System.Security.Principal;

namespace AgentBroker.ClaudeAdapter;

/// <summary>Pure validation of already retrieved local pipe security metadata.</summary>
public static class NativePipeSecurityPolicy
{
    // Generic write/all, delete, write-owner/DACL, write-data/attributes/EA and create-pipe-instance.
    private const int WriteOrControl = 0x500D0116;

    public static bool IsCurrentUserPipe(RawSecurityDescriptor descriptor, SecurityIdentifier currentUser)
    {
        if (descriptor.Owner is not { } owner || !owner.Equals(currentUser) ||
            (descriptor.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0 || descriptor.DiscretionaryAcl is not { } dacl)
            return false;
        foreach (GenericAce ace in dacl)
        {
            if (ace is not CommonAce common || common.IsCallback) return false;
            if (common.AceQualifier == AceQualifier.AccessDenied) continue;
            if (common.AceQualifier != AceQualifier.AccessAllowed) return false;
            if ((common.AccessMask & WriteOrControl) == 0) continue;
            var sid = common.SecurityIdentifier;
            if (!sid.Equals(currentUser) && !sid.IsWellKnown(WellKnownSidType.LocalSystemSid) &&
                !sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)) return false;
        }
        return true;
    }
}
