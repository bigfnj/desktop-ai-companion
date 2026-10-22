#requires -Version 7
<#
.SYNOPSIS
    Read AgentBroker peer metadata and optionally probe pinned public HTTPS information.
.DESCRIPTION
    Uses the production status client and pipe security with an exact worker PID. Prints
    endpoints, public certificate fingerprints, approval state, counts and fixed errors.
    Never opens settings, identity files, private keys, agent messages or client config.
    ProbePeers sends only GET /broker/info, with no client certificate, proxy or redirects.
    It does not initiate/approve pairing or change trust, firewall rules or network profiles.
.EXAMPLE
    pwsh -NoProfile -File tools/agentbroker/Test-AgentBrokerLan.ps1 -CompileOnly
.EXAMPLE
    pwsh -NoProfile -File tools/agentbroker/Test-AgentBrokerLan.ps1 -WorkerProcessId 42932 -ProbePeers
#>
[CmdletBinding()]
param(
    [ValidateRange(2, 2147483647)][int]$WorkerProcessId,
    [switch]$ProbePeers,
    [switch]$CompileOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'This diagnostic requires Windows.' }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$parts = [Collections.Generic.List[string]]::new()
$parts.Add(@'
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Threading;
global using System.Threading.Tasks;
'@)
foreach ($relative in @('tools\AgentBroker.Worker\ControlProtocol.cs',
                         'tools\AgentBroker.Worker\ControlPipeSecurity.cs',
                         'modules\AgentBroker\BrokerControlClient.cs')) {
    $source = [IO.File]::ReadAllText((Join-Path $repo $relative))
    $match = [regex]::Match($source, '(?m)^namespace ([A-Za-z0-9_.]+);\s*$')
    if (-not $match.Success -or [regex]::Matches($source, '(?m)^namespace ').Count -ne 1) {
        throw 'An expected diagnostic source namespace changed.'
    }
    $preamble = [regex]::Replace($source.Substring(0, $match.Index), '(?m)^using ', 'using global::')
    $parts.Add('namespace ' + $match.Groups[1].Value + " {`n" + $preamble +
        $source.Substring($match.Index + $match.Length) + "`n}")
}
$parts.Add(@'
namespace DesktopAICompanion.AgentBroker {
using global::AgentBroker.Protocol;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

public sealed class BrokerLanPeerMetadata {
    public string Endpoint { get; set; } = "";
    public string CertificateSha256 { get; set; } = "";
    public bool Discovered { get; set; }
    public bool Paired { get; set; }
    public bool Pending { get; set; }
    public bool Outbound { get; set; }
    public bool LocalApproved { get; set; }
    public bool RemoteApproved { get; set; }
    public int ExpiresInSeconds { get; set; }
}
public sealed class BrokerLanPublicProbe {
    public string Endpoint { get; set; } = "";
    public string ExpectedCertificateSha256 { get; set; } = "";
    public string ObservedCertificateSha256 { get; set; } = "";
    public bool CertificateSeen { get; set; }
    public bool TcpConnected { get; set; }
    public string LocalSourceAddress { get; set; } = "";
    public bool CertificatePinMatches { get; set; }
    public int HttpStatus { get; set; }
    public bool PublicInfoMatches { get; set; }
    public long ElapsedMs { get; set; }
    public string Code { get; set; } = "not_probed";
}
public sealed class BrokerLanInterface {
    public int InterfaceIndex { get; set; }
    public string Address { get; set; } = "";
    public int PrefixLength { get; set; }
}
public sealed class BrokerLanDiagnosticResult {
    public bool Success { get; set; }
    public string Code { get; set; } = "unavailable";
    public string? LocalLanEndpoint { get; set; }
    public string? LocalCertificateSha256 { get; set; }
    public int SessionCount { get; set; }
    public int DiscoveredCount { get; set; }
    public int PairedCount { get; set; }
    public int PendingCount { get; set; }
    public string[] Errors { get; set; } = Array.Empty<string>();
    public BrokerLanPeerMetadata[] Peers { get; set; } = Array.Empty<BrokerLanPeerMetadata>();
    public BrokerLanInterface[] PrivateInterfaces { get; set; } = Array.Empty<BrokerLanInterface>();
    public BrokerLanPublicProbe[] PublicInfoProbes { get; set; } = Array.Empty<BrokerLanPublicProbe>();
}
public static class BrokerLanDiagnostic {
    private static string SafeCode(string? value) => value is not null &&
        System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z0-9_.-]{1,100}$") ? value : "unclassified_error";
    private static bool PrivateV4(IPAddress address) {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        byte[] b = address.GetAddressBytes();
        return b[0] == 10 || b[0] == 172 && b[1] >= 16 && b[1] <= 31 || b[0] == 192 && b[1] == 168;
    }
    private static Uri Endpoint(string value) {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !IPAddress.TryParse(uri.Host, out var ip) || !PrivateV4(ip)) throw new IOException("private_endpoint_required");
        return uri;
    }
    private static BrokerLanInterface[] Interfaces() {
        var result = new List<BrokerLanInterface>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces()) {
            try {
                if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                var properties = adapter.GetIPProperties();
                int index = properties.GetIPv4Properties()?.Index ?? 0;
                foreach (var address in properties.UnicastAddresses.Where(a => PrivateV4(a.Address)))
                    result.Add(new BrokerLanInterface { InterfaceIndex = index, Address = address.Address.ToString(), PrefixLength = address.PrefixLength });
            } catch (NetworkInformationException) { }
            if (result.Count >= 32) break;
        }
        return result.Take(32).ToArray();
    }
    private static async Task<BrokerLanPublicProbe> ProbeAsync(BrokerLanPeerMetadata peer) {
        var result = new BrokerLanPublicProbe { Endpoint = peer.Endpoint, ExpectedCertificateSha256 = peer.CertificateSha256 };
        var elapsed = Stopwatch.StartNew();
        try {
            Uri endpoint = Endpoint(peer.Endpoint);
            if (!System.Text.RegularExpressions.Regex.IsMatch(peer.CertificateSha256, "^[A-Fa-f0-9]{64}$"))
                throw new IOException("invalid_public_pin");
            using var handler = new SocketsHttpHandler { UseProxy = false, UseCookies = false,
                AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(3) };
            handler.ConnectCallback = async (_, cancellation) => {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try {
                    await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse(endpoint.Host), endpoint.Port), cancellation).ConfigureAwait(false);
                    result.TcpConnected = true;
                    result.LocalSourceAddress = ((IPEndPoint)socket.LocalEndPoint!).Address.ToString();
                    return new NetworkStream(socket, true);
                } catch { socket.Dispose(); throw; }
            };
            // Explicitly decline client identity. This public-info diagnostic never asks CNG,
            // a credential file or a certificate store to use an agent/broker private key.
            handler.SslOptions = new SslClientAuthenticationOptions {
                ClientCertificates = new X509CertificateCollection(),
                LocalCertificateSelectionCallback = (_, _, _, _, _) => null,
                RemoteCertificateValidationCallback = (_, certificate, _, _) => {
                    if (certificate is null) return false;
                    result.CertificateSeen = true;
                    result.ObservedCertificateSha256 = certificate.GetCertHashString(HashAlgorithmName.SHA256);
                    result.CertificatePinMatches = result.ObservedCertificateSha256.Equals(peer.CertificateSha256, StringComparison.OrdinalIgnoreCase);
                    return result.CertificatePinMatches;
                }
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var response = await client.GetAsync(new Uri(endpoint, "broker/info"), HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            result.HttpStatus = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode) { result.Code = "http_refused"; return result; }
            using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var body = new MemoryStream(); byte[] buffer = new byte[4096]; int read;
            while ((read = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) != 0) {
                if (body.Length + read > 32768) throw new IOException("public_info_oversized");
                body.Write(buffer, 0, read);
            }
            using var json = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            var root = json.RootElement;
            result.PublicInfoMatches = Endpoint(root.GetProperty("endpoint").GetString()!) == endpoint &&
                root.GetProperty("certificateSha256").GetString()?.Equals(peer.CertificateSha256, StringComparison.OrdinalIgnoreCase) == true;
            result.Code = result.PublicInfoMatches ? "ok" : "public_info_mismatch";
        } catch (OperationCanceledException) { result.Code = "timeout"; }
          catch (HttpRequestException error) { result.Code = "http_" + error.HttpRequestError.ToString(); }
          catch (IOException error) { result.Code = SafeCode(error.Message); }
          catch (JsonException) { result.Code = "invalid_public_info"; }
          catch (Exception) { result.Code = "probe_unavailable"; }
        finally { result.ElapsedMs = elapsed.ElapsedMilliseconds; }
        return result;
    }
    public static async Task<string> RunAsync(string data, int pid, bool probePeers) {
        var result = new BrokerLanDiagnosticResult();
        try {
            var response = await new BrokerControlClient().SendAsync(data, new ControlRequest("status"), CancellationToken.None, pid).ConfigureAwait(false);
            result.Code = SafeCode(response.Code);
            if (!response.Success || response.Snapshot is not { } snapshot)
                return JsonSerializer.Serialize(result);
            result.Success = true;
            result.LocalLanEndpoint = snapshot.LanEndpoint;
            result.LocalCertificateSha256 = snapshot.CertificateSha256;
            result.SessionCount = snapshot.Sessions.Length;
            result.DiscoveredCount = snapshot.Discovered.Length;
            result.PairedCount = snapshot.Paired.Length;
            result.PendingCount = snapshot.PairingOffers.Length;
            result.Errors = snapshot.AdapterErrors.Values.Select(SafeCode).Distinct(StringComparer.Ordinal).Take(16).ToArray();
            var peers = new Dictionary<string, BrokerLanPeerMetadata>(StringComparer.OrdinalIgnoreCase);
            BrokerLanPeerMetadata Peer(PeerView peer) {
                string key = peer.Endpoint + "|" + peer.CertificateSha256;
                if (!peers.TryGetValue(key, out var value)) peers.Add(key, value = new BrokerLanPeerMetadata {
                    Endpoint = peer.Endpoint, CertificateSha256 = peer.CertificateSha256 });
                return value;
            }
            foreach (var peer in snapshot.Discovered.Take(32)) Peer(peer).Discovered = true;
            foreach (var peer in snapshot.Paired.Take(32)) Peer(peer).Paired = true;
            foreach (var offer in snapshot.PairingOffers.Take(32)) {
                var peer = Peer(offer.Peer); peer.Pending = true; peer.Outbound = offer.Outbound;
                peer.LocalApproved = offer.LocalApproved; peer.RemoteApproved = offer.RemoteApproved;
                peer.ExpiresInSeconds = Math.Max(0, (int)(offer.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds);
            }
            result.Peers = peers.Values.Take(32).ToArray();
            result.PrivateInterfaces = Interfaces();
            if (probePeers)
                result.PublicInfoProbes = await Task.WhenAll(result.Peers.Take(4).Select(ProbeAsync)).ConfigureAwait(false);
        } catch (OperationCanceledException) { result.Code = "control_timeout"; }
          catch (UnauthorizedAccessException) { result.Code = "control_security_refused"; }
          catch (IOException error) { result.Code = SafeCode(error.Message); }
          catch (Exception) { result.Code = "control_unavailable"; }
        return JsonSerializer.Serialize(result);
    }
}
}
'@)
Add-Type -TypeDefinition ([string]::Join("`n", $parts)) -CompilerOptions '/nullable:enable'
if ($CompileOnly) { Write-Host 'SourceCompiled=True PipeOpened=False NetworkProbed=False'; return }
if (-not $PSBoundParameters.ContainsKey('WorkerProcessId')) { throw 'An exact current WorkerProcessId is required.' }
$data = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'DesktopAICompanion\modules\agentbroker'
[DesktopAICompanion.AgentBroker.BrokerLanDiagnostic]::RunAsync($data, $WorkerProcessId, $ProbePeers.IsPresent).GetAwaiter().GetResult()
