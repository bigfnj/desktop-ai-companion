using AgentBroker.Protocol;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.AgentBroker;

public sealed partial class AgentBrokerModule
{
    private readonly HashSet<Guid> seenDiscoveryPrompts = [];
    private bool discoveryPromptOpen;
    private DateTimeOffset nextDiscoveryPrompt;

    private void ResetDiscoveryPrompts()
    { seenDiscoveryPrompts.Clear(); discoveryPromptOpen = false; nextDiscoveryPrompt = default; }

    private void AskAboutDiscovery(BrokerSnapshot view)
    {
        if (questions is null || run is null || discoveryPromptOpen || DateTimeOffset.UtcNow < nextDiscoveryPrompt ||
            seenDiscoveryPrompts.Count >= 32 || view.PairingOffers.Length != 0) return;
        PeerView? peer = view.Discovered.FirstOrDefault(p => !seenDiscoveryPrompts.Contains(p.BrokerId) &&
            !view.Paired.Any(t => t.BrokerId == p.BrokerId) && !view.PairingOffers.Any(o => o.Peer.BrokerId == p.BrokerId));
        if (peer is null) return;
        seenDiscoveryPrompts.Add(peer.BrokerId);
        discoveryPromptOpen = true;
        nextDiscoveryPrompt = DateTimeOffset.UtcNow.AddMinutes(1);
        _ = AskDiscoveredAsync(peer, generation, run.Token);
    }

    private async Task AskDiscoveredAsync(PeerView peer, int current, CancellationToken token)
    {
        try
        {
            var service = questions;
            if (service is null || shutDown) return;
            var answer = await service.AskAsync(Id, new TrayQuestion
            {
                Key = "discover." + peer.BrokerId.ToString("N"), Title = "Another broker discovered",
                Text = "Request pairing with " + Safe(peer.Name, 32) +
                    "? This does not share sessions yet. Compare the complete pairing code, then allow current and future discoverable sessions on both workstations.",
                Choices = [new("allow", "Request pairing"), new("later", "Not now")],
                TimeoutChoiceId = "later", Timeout = TimeSpan.FromMinutes(2), RunsWhileHeld = false,
            }, token);
            if (shutDown || generation != current || token.IsCancellationRequested) return;
            if (answer.Outcome == TrayQuestionOutcome.Chosen && answer.ChoiceId == "allow")
                await SendAsync(new("pair-request", Peer: peer));
        }
        catch (Exception) { /* A refused question never grants trust. The tray remains available. */ }
        finally { if (generation == current) discoveryPromptOpen = false; }
    }
}
