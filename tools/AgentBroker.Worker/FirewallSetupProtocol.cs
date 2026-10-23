using System.Globalization;

namespace AgentBroker.Firewall;

// Shared with the module. This command never accepts an executable, script or address scope.
public static class FirewallSetupProtocol
{
    public const string Command = "--allow-lan-access";
    public const string PortOption = "--port";

    public static bool TryParse(string[] args, out int port)
    {
        port = 0;
        return args.Length == 3 && args[0] == Command && args[1] == PortOption &&
            int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out port) &&
            port is >= 1 and <= 65535;
    }

    public static string Arguments(int port)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        return Command + " " + PortOption + " " + port.ToString(CultureInfo.InvariantCulture);
    }
}

public enum FirewallSetupExitCode
{
    Allowed = 0,
    InvalidArguments = 2,
    AdministratorRequired = 5,
    UnsupportedPlatform = 6,
    ConflictingRule = 7,
    PolicyRestricted = 8,
    VerificationFailed = 9,
    Unavailable = 10,
    ExistingBlock = 11,
}
