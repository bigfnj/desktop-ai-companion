#requires -Version 7.4
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
    [switch]$CompileOnly,
    [switch]$SelfTest
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
                         'modules\AgentBroker\BrokerControlClient.cs',
                         'tools\AgentBroker.Worker\Security\CertificateContinuity.cs')) {
    $source = [IO.File]::ReadAllText((Join-Path $repo $relative))
    if ($relative.EndsWith('CertificateContinuity.cs', [StringComparison]::Ordinal)) {
        $loader = 'X509CertificateLoader.LoadCertificate(anchorDer)'
        if ([regex]::Matches($source, [regex]::Escape($loader)).Count -ne 1) { throw 'The shared public-certificate loader boundary changed.' }
        $source = $source.Replace($loader, 'global::DesktopAICompanion.AgentBroker.BrokerLanDiagnostic.LoadPublicCertificate(anchorDer)')
    }
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
    public Guid BrokerId { get; set; }
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
    public string CertificateVerificationMode { get; set; } = "not_verified";
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
    // Public DER only, supported on .NET8; continuity decisions stay in the exact shared verifier source.
    internal static X509Certificate2 LoadPublicCertificate(byte[] der) {
        if (X509Certificate2.GetCertContentType(der) != X509ContentType.Cert) throw new CryptographicException("public_certificate_required");
        return X509Certificate2.CreateFromPem(PemEncoding.WriteString("CERTIFICATE", der));
    }
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
    internal static SslClientAuthenticationOptions PublicTlsOptions(BrokerLanPeerMetadata peer, BrokerLanPublicProbe result) => new() {
        ClientCertificates = new X509CertificateCollection(),
        LocalCertificateSelectionCallback = (_, _, _, _, _) => null!,
        RemoteCertificateValidationCallback = (_, certificate, _, _) => {
            if (certificate is not X509Certificate2 cert) return false;
            result.CertificateSeen = true;
            result.ObservedCertificateSha256 = cert.GetCertHashString(HashAlgorithmName.SHA256);
            result.CertificatePinMatches = global::AgentBroker.Worker.Security.CertificateContinuity.MatchesPinnedCertificate(
                cert, peer.BrokerId, peer.CertificateSha256, DateTimeOffset.UtcNow);
            result.CertificateVerificationMode = !result.CertificatePinMatches ? "not_verified" :
                result.ObservedCertificateSha256.Equals(peer.CertificateSha256, StringComparison.OrdinalIgnoreCase) ? "exact_pin" : "authenticated_renewal";
            return result.CertificatePinMatches;
        }
    };
    private static async Task<BrokerLanPublicProbe> ProbeAsync(BrokerLanPeerMetadata peer) {
        var result = new BrokerLanPublicProbe { Endpoint = peer.Endpoint, ExpectedCertificateSha256 = peer.CertificateSha256 };
        var elapsed = Stopwatch.StartNew();
        try {
            Uri endpoint = Endpoint(peer.Endpoint);
            if (!System.Text.RegularExpressions.Regex.IsMatch(peer.CertificateSha256, "^[A-Fa-f0-9]{64}$"))
                throw new IOException("invalid_public_pin");
            if (peer.BrokerId == Guid.Empty) throw new IOException("invalid_public_broker");
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
            handler.SslOptions = PublicTlsOptions(peer, result);
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
            result.PublicInfoMatches = MatchesPublicInfo(root, peer, endpoint);
            result.Code = result.PublicInfoMatches ? "ok" : "public_info_mismatch";
        } catch (OperationCanceledException) { result.Code = "timeout"; }
          catch (HttpRequestException error) { result.Code = "http_" + error.HttpRequestError.ToString(); }
          catch (IOException error) { result.Code = SafeCode(error.Message); }
          catch (JsonException) { result.Code = "invalid_public_info"; }
          catch (Exception) { result.Code = "probe_unavailable"; }
        finally { result.ElapsedMs = elapsed.ElapsedMilliseconds; }
        return result;
    }
    internal static bool MatchesPublicInfo(JsonElement root, BrokerLanPeerMetadata peer, Uri endpoint) =>
        root.GetProperty("brokerId").GetGuid() == peer.BrokerId && Endpoint(root.GetProperty("endpoint").GetString()!) == endpoint &&
        root.GetProperty("certificateSha256").GetString()?.Equals(peer.CertificateSha256, StringComparison.OrdinalIgnoreCase) == true;
    public static string SelfTest() {
        int checks = 0;
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException("LAN_SELFTEST_FAIL:" + name); checks++; Console.WriteLine("PASS: LAN_" + name); }
        var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        using var key = RSA.Create(3072); using var other = RSA.Create(3072);
        X509Certificate2 Create(RSA rsa, DateTimeOffset start, DateTimeOffset end, X509Extension? proof = null) {
            var request = new CertificateRequest("CN=AgentBroker-" + id.ToString("N"), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1"), new("1.3.6.1.5.5.7.3.2") }, false));
            var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("broker-" + id.ToString("N") + ".local"); request.CertificateExtensions.Add(san.Build());
            if (proof is not null) request.CertificateExtensions.Add(proof);
            return request.CreateSelfSigned(start, end);
        }
        using var legacy = Create(key, now.AddDays(-1), now.AddYears(1));
        using var anchor = Create(key, now.AddYears(-6), now.AddYears(-1));
        var proof = global::AgentBroker.Worker.Security.CertificateContinuity.CreateAnchorExtension(anchor, id);
        using var renewed = Create(key, now.AddDays(-1), now.AddYears(1), proof);
        using var wrongKey = Create(other, now.AddDays(-1), now.AddYears(1), proof);
        using var expired = Create(key, now.AddDays(-2), now.AddDays(-1), proof);
        string pin = anchor.GetCertHashString(HashAlgorithmName.SHA256);
        BrokerLanPeerMetadata Peer(Guid broker, string fingerprint) => new() { BrokerId = broker, CertificateSha256 = fingerprint, Endpoint = "https://10.0.0.1:7433/" };
        bool Validate(BrokerLanPeerMetadata peer, X509Certificate2 certificate, string expectedMode = "not_verified") {
            var observation = new BrokerLanPublicProbe();
            using var handler = new SocketsHttpHandler { SslOptions = PublicTlsOptions(peer, observation) };
            bool matches = handler.SslOptions.RemoteCertificateValidationCallback!(new object(), certificate, null, SslPolicyErrors.RemoteCertificateChainErrors);
            if (observation.CertificateVerificationMode != expectedMode) throw new InvalidOperationException("LAN_SELFTEST_FAIL:VERIFICATION_MODE");
            return matches;
        }
        Check(Validate(Peer(id, legacy.GetCertHashString(HashAlgorithmName.SHA256)), legacy, "exact_pin"), "LEGACY_EXACT_PIN");
        Check(renewed.GetCertHashString(HashAlgorithmName.SHA256) != pin && Validate(Peer(id, pin), renewed, "authenticated_renewal"), "RENEWED_ANCHOR");
        Check(!Validate(Peer(Guid.NewGuid(), pin), renewed), "BROKER_ID_REFUSED");
        Check(!Validate(Peer(id, pin), wrongKey), "CHANGED_KEY_REFUSED");
        Check(!Validate(Peer(id, new string('0', 64)), renewed), "CHANGED_PIN_REFUSED");
        Check(!Validate(Peer(id, pin), expired), "EXPIRED_LIVE_REFUSED");
        var options = PublicTlsOptions(Peer(id, pin), new BrokerLanPublicProbe());
        Check(options.ClientCertificates!.Count == 0 && options.LocalCertificateSelectionCallback!(new object(), "synthetic", new X509CertificateCollection { legacy }, null, Array.Empty<string>()) is null, "PUBLIC_ONLY_CLIENT_DECLINED");
        bool invalid = false; try { using var ignored = LoadPublicCertificate(new byte[] { 0, 1, 2 }); } catch (CryptographicException) { invalid = true; }
        Check(invalid, "PUBLIC_DECODER_REFUSES_OTHER_CONTENT");
        var correct = JsonSerializer.SerializeToElement(new { brokerId = id, endpoint = "https://10.0.0.1:7433/", certificateSha256 = pin });
        var different = JsonSerializer.SerializeToElement(new { brokerId = Guid.NewGuid(), endpoint = "https://10.0.0.1:7433/", certificateSha256 = pin });
        Check(MatchesPublicInfo(correct, Peer(id, pin), new Uri("https://10.0.0.1:7433/")), "PUBLIC_METADATA_MATCH");
        Check(!MatchesPublicInfo(different, Peer(id, pin), new Uri("https://10.0.0.1:7433/")), "PUBLIC_METADATA_BROKER_REFUSED");
        return "PASS: " + checks + " AgentBroker LAN certificate checks; no connections opened";
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
                string key = peer.BrokerId.ToString("D") + "|" + peer.Endpoint + "|" + peer.CertificateSha256;
                if (!peers.TryGetValue(key, out var value)) peers.Add(key, value = new BrokerLanPeerMetadata {
                    BrokerId = peer.BrokerId, Endpoint = peer.Endpoint, CertificateSha256 = peer.CertificateSha256 });
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
if ($SelfTest) { [DesktopAICompanion.AgentBroker.BrokerLanDiagnostic]::SelfTest(); return }
if ($CompileOnly) { Write-Host 'SourceCompiled=True PipeOpened=False NetworkProbed=False'; return }
if (-not $PSBoundParameters.ContainsKey('WorkerProcessId')) { throw 'An exact current WorkerProcessId is required.' }
$data = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'DesktopAICompanion\modules\agentbroker'
[DesktopAICompanion.AgentBroker.BrokerLanDiagnostic]::RunAsync($data, $WorkerProcessId, $ProbePeers.IsPresent).GetAwaiter().GetResult()
