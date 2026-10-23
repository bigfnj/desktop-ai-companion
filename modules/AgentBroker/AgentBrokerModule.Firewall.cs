using AgentBroker.Firewall;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.AgentBroker;

public sealed partial class AgentBrokerModule
{
    private string lanFirewallStatus = "";
    private bool lanFirewallActionOpen;

    private string LanAccessText() => lanFirewallStatus.Length != 0 ? lanFirewallStatus :
        "Allow LAN access can add this worker's inbound TCP rule for Private networks and the local subnet. Windows administrator approval is required. Run it on each workstation that needs to receive pairing requests.";

    private string SetLanFirewallStatus(string value)
    {
        lanFirewallStatus = value;
        NotifyInfoChanged();
        return value;
    }

    private bool TryGetLanFirewallRequest(out LanFirewallRequest request)
    {
        request = default;
        if (shutDown || !settings.Enabled || !settings.EnableLan || !settings.Valid ||
            !ownsWorker || run is null || run.IsCancellationRequested ||
            !Uri.TryCreate(snapshot?.LanEndpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps || endpoint.Port != settings.LanPort ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
            endpoint.AbsolutePath != "/") return false;
        return LanFirewallRequest.TryCreate(typeof(AgentBrokerModule).Assembly.Location, settings.LanPort, out request);
    }

    private async Task<string> AllowLanAccessAsync()
    {
        if (lanFirewallActionOpen) return "LAN access setup is already awaiting an answer or administrator approval.";
        if (!TryGetLanFirewallRequest(out var request))
            return SetLanFirewallStatus("Start this companion's own LAN broker before allowing access. Apply Enable AgentBroker, LAN discovery and a valid LAN port, then wait for Running status.");
        if (FirewallLauncher.IsPending) return SetLanFirewallStatus(LanFirewallResultText(new(LanFirewallLaunchOutcome.AlreadyPending)));
        ITrayQuestions? questionHost = questions;
        if (questionHost is null) return SetLanFirewallStatus("LAN access confirmation is unavailable. No firewall rule was requested.");
        int current = generation;
        CancellationToken token = run!.Token;
        lanFirewallActionOpen = true;
        bool launchSubmitted = false;
        try
        {
            TrayAnswer answer = await questionHost.AskAsync(Id, new TrayQuestion
            {
                Key = "firewall.allow-lan", Title = "Allow LAN access on this PC?",
                Text = "Allow this broker to receive TCP " + request.Port +
                    " from the local subnet on Private networks? Windows administrator approval follows. Pairing still requires approval on both PCs. This action adds no Public-network rule.",
                Choices = [new("allow", "Allow access"), new("cancel", "Cancel")],
                TimeoutChoiceId = "cancel", Timeout = TimeSpan.FromMinutes(2), RunsWhileHeld = false
            }, token);
            // Consent applies only to the running worker and applied port that were shown.
            // A late answer after shutdown or any settings restart grants nothing.
            if (shutDown || generation != current || token.IsCancellationRequested ||
                !TryGetLanFirewallRequest(out var liveRequest) || liveRequest != request)
                return "LAN access setup was cancelled because the broker connection changed.";
            if (answer.Outcome != TrayQuestionOutcome.Chosen || answer.ChoiceId != "allow")
                return SetLanFirewallStatus("LAN access setup cancelled. No firewall rule was requested.");
            SetLanFirewallStatus("Waiting for Windows administrator approval to configure local LAN access.");
            launchSubmitted = true;
            LanFirewallLaunchResult result = await FirewallLauncher.AllowAsync(request, token);
            if (shutDown || generation != current || token.IsCancellationRequested)
                return "The broker connection changed after Windows setup was requested. The firewall outcome is unconfirmed; check Windows Firewall before retrying.";
            return SetLanFirewallStatus(LanFirewallResultText(result));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return launchSubmitted
            ? "The broker connection changed after Windows setup was requested. The firewall outcome is unconfirmed; check Windows Firewall before retrying."
            : "LAN access setup was cancelled because the broker connection changed."; }
        catch (Exception)
        {
            if (shutDown || generation != current) return "LAN access setup ended after the broker connection changed.";
            return SetLanFirewallStatus("LAN access setup is unavailable. No verified firewall result was received.");
        }
        finally { lanFirewallActionOpen = false; }
    }

    private static string LanFirewallResultText(LanFirewallLaunchResult result) => result.Outcome switch
    {
        LanFirewallLaunchOutcome.Cancelled => "Windows administrator approval was cancelled. No firewall rule was requested.",
        LanFirewallLaunchOutcome.AlreadyPending => "Windows LAN access setup is still pending. Finish the existing administrator prompt or wait for that operation to end before retrying.",
        LanFirewallLaunchOutcome.InvalidScope => "The packaged worker path is unavailable or redirected. LAN access was not configured.",
        LanFirewallLaunchOutcome.TimedOut => "Windows did not report a firewall result in time. The outcome is unconfirmed; check Windows Firewall before retrying.",
        LanFirewallLaunchOutcome.Unavailable => "Windows LAN access setup is unavailable. No verified firewall result was received.",
        LanFirewallLaunchOutcome.Exited => result.ExitCode switch
        {
            (int)FirewallSetupExitCode.Allowed => "The requested Private-network TCP allow rule is configured on this workstation. Reachability and pairing remain unconfirmed; both PCs still need LAN access and pairing approval.",
            (int)FirewallSetupExitCode.InvalidArguments => "LAN access setup rejected the worker or port scope. No verified firewall rule was configured.",
            (int)FirewallSetupExitCode.AdministratorRequired => "Windows administrator approval is required to configure LAN access.",
            (int)FirewallSetupExitCode.UnsupportedPlatform => "This Windows environment does not support the LAN firewall setup action.",
            (int)FirewallSetupExitCode.ConflictingRule => "A conflicting firewall rule already uses this broker's rule identity. LAN access was not changed; review it in Windows Firewall.",
            (int)FirewallSetupExitCode.PolicyRestricted => "Windows policy prevents the requested local firewall rule from taking effect. Contact the administrator for Private-network TCP access to this broker.",
            (int)FirewallSetupExitCode.VerificationFailed => "Windows did not verify the requested firewall rule. LAN reachability remains unconfirmed.",
            (int)FirewallSetupExitCode.ExistingBlock => "An existing Windows Firewall block may prevent LAN connections. Review that block; reachability remains unconfirmed.",
            _ => "Windows did not confirm LAN firewall access. Review Windows Firewall and try again."
        },
        _ => "Windows did not confirm LAN firewall access."
    };
}
