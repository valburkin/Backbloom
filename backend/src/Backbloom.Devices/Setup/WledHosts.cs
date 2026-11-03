using System.Net;
using System.Net.Sockets;
using Backbloom.Discovery;

namespace Backbloom.Devices.Setup;

/// <summary>
/// Which boards the guided setup may talk to: devices on the local network. Any host name is accepted (<c>.local</c>,
/// <c>wled.lan</c>, <c>fritz.box</c>, <c>*.home.arpa</c>, a single label…); what counts is the address it resolves to:
/// one of a private, link-local or unique-local network, or WLED's access point address 4.3.2.1 while this computer is
/// on that access point (an interface address in 4.3.2.0/24). Loopback, multicast, broadcast and public addresses are
/// refused, so the setup can't be used to reach other services on this computer or on the internet.
/// </summary>
public static class WledHosts
{
    /// <summary>The address of a WLED board's own access point (WLED-AP).</summary>
    public static readonly IPAddress AccessPoint = IPAddress.Parse("4.3.2.1");

    /// <summary>A syntactically valid host name or IP address (before resolving it).</summary>
    public static bool IsHostName(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return host.Length > 0 && (IPAddress.TryParse(host, out _) || Uri.CheckHostName(host.TrimEnd('.')) == UriHostNameType.Dns);
    }

    /// <summary>An address on the local network (see the class).</summary>
    /// <param name="localAddresses">This computer's interface addresses (for 4.3.2.1); the live ones by default.</param>
    public static bool IsLanAddress(IPAddress address, Func<IEnumerable<IPAddress>>? localAddresses = null)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        if (address.Equals(AccessPoint))
        {
            return OnAccessPoint(localAddresses ?? (() => LocalNetwork.Addresses().Select(a => a.Address)));
        }

        // (255.255.255.255, 0.0.0.0 and multicast aren't in these ranges; a directed broadcast address can't take the
        // TCP connection HTTP needs)
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    // connected to a WLED access point: an address of its 4.3.2.0/24 network (4.3.2.1 itself is public otherwise)
    private static bool OnAccessPoint(Func<IEnumerable<IPAddress>> localAddresses) =>
        localAddresses().Any(a => a.AddressFamily == AddressFamily.InterNetwork && a.GetAddressBytes() is [4, 3, 2, _] && !a.Equals(AccessPoint));
}
