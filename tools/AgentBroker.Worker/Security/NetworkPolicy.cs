using System.Net;

namespace AgentBroker.Worker.Security;

public static class NetworkPolicy
{
    public static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    public static bool PrivateV4(IPAddress address, bool allowLoopback = true)
    {
        address = Normalize(address);
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        byte[] b = address.GetAddressBytes();
        return (allowLoopback && b[0] == 127) || b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }
    public static Uri Endpoint(string value, IPAddress? observed = null)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 ||
            uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !IPAddress.TryParse(uri.Host, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || !PrivateV4(ip) ||
            (observed is not null && !Normalize(ip).Equals(Normalize(observed)))) throw new InvalidDataException("Peer endpoint must match its direct private IPv4 TLS address.");
        return uri;
    }
    public static void Advertisement(BrokerAdvertisement peer, IPAddress? observed = null)
    {
        if (peer.BrokerId == Guid.Empty || string.IsNullOrWhiteSpace(peer.Name) || peer.Name.Length > 80 || peer.Name.Any(char.IsControl) ||
            peer.CertificateSha256 is null || peer.CertificateSha256.Length != 64 || !peer.CertificateSha256.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid broker identity metadata.");
        Endpoint(peer.Endpoint, observed);
    }
}
