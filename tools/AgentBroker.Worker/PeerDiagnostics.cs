using AgentBroker.Core;
using AgentBroker.Protocol;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Authentication;
using System.Text.Json;

namespace AgentBroker.Worker;

/// <summary>Explicit local management checks; no session contents leave the worker.</summary>
public static class PeerDiagnostics
{
    public static async Task<PeerDiagnosticView[]> CollectAsync(BrokerRuntime runtime, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var peers = runtime.Store.Peers().OrderBy(peer => peer.BrokerId).Take(32).ToArray();
        var authorizations = peers.ToDictionary(peer => peer.BrokerId, runtime.Store.CapturePeerAuthorization);
        var results = new ConcurrentDictionary<Guid, PeerDiagnosticView>();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await Parallel.ForEachAsync(peers, new ParallelOptions
            { MaxDegreeOfParallelism = 4, CancellationToken = budget.Token }, async (peer, stop) =>
            {
                stop.ThrowIfCancellationRequested();
                var authorization = authorizations[peer.BrokerId];
                if (authorization is null || !runtime.Store.IsPeerAuthorized(authorization))
                {
                    results[peer.BrokerId] = Failure(peer, "trust_changed");
                    return;
                }
                try
                {
                    // Preserve the overall deadline even if a transport observes cancellation late.
                    // No continuation can publish a late result after this wait has ended.
                    var directory = await runtime.Federation.GetDirectoryAsync(peer, stop).WaitAsync(stop);
                    stop.ThrowIfCancellationRequested();
                    if (!runtime.Store.IsPeerAuthorized(authorization))
                        results[peer.BrokerId] = Failure(peer, "trust_changed");
                    else if (directory is null || directory.BrokerId != peer.BrokerId || directory.Sessions is null ||
                        directory.Sessions.Count > 256 || directory.Sessions.Any(session => session is null ||
                            session.Address is null || session.Address.BrokerId != peer.BrokerId || session.IsPrivate))
                        results[peer.BrokerId] = Failure(peer, "invalid_directory");
                    else
                        results[peer.BrokerId] = new(peer.BrokerId, peer.Endpoint, peer.CertificateSha256,
                            true, "ok", directory.Sessions.Count, "confirmed", DateTimeOffset.UtcNow);
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
                    HttpRequestException or JsonException or BrokerException or OperationCanceledException)
                {
                    token.ThrowIfCancellationRequested();
                    results[peer.BrokerId] = Failure(peer, runtime.Store.IsPeerAuthorized(authorization)
                        ? FailureCode(error) : "trust_changed");
                }
            });
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested && !token.IsCancellationRequested)
        { /* Completed and attempted checks remain distinct from peers not started within the budget. */ }
        token.ThrowIfCancellationRequested();
        return peers.Select(peer =>
        {
            var result = results.GetValueOrDefault(peer.BrokerId);
            // Another peer may have used the remaining budget after this observation completed.
            // Re-pairing or revocation in that interval must not publish an earlier confirmed result.
            if (result?.Success == true && (authorizations[peer.BrokerId] is not { } authorization ||
                !runtime.Store.IsPeerAuthorized(authorization))) return Failure(peer, "trust_changed");
            return result ?? new PeerDiagnosticView(peer.BrokerId, peer.Endpoint, peer.CertificateSha256,
                false, "not_checked_budget", null, "not_checked", null);
        }).ToArray();
    }

    private static PeerDiagnosticView Failure(PeerTrust peer, string code) => new(peer.BrokerId,
        peer.Endpoint, peer.CertificateSha256, false, code, null, "not_confirmed", DateTimeOffset.UtcNow);

    private static string FailureCode(Exception error) => error switch
    {
        UnauthorizedAccessException => "trust_changed",
        OperationCanceledException => "timeout",
        InvalidDataException or JsonException => "invalid_directory",
        HttpRequestException http when http.HttpRequestError == HttpRequestError.SecureConnectionError ||
            http.InnerException is AuthenticationException => "tls_failed",
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden } => "peer_refused",
        HttpRequestException { StatusCode: not null } => "http_failed",
        _ => "transport_failed",
    };
}
