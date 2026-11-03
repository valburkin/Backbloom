using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Backbloom.Discovery;

/// <summary>An IPv4 address of this computer on a network interface that is up.</summary>
/// <param name="Broadcast">The interface's directed broadcast address (null when its mask is unknown).</param>
/// <param name="SupportsMulticast">The interface can send multicast (mDNS, SSDP).</param>
public sealed record LocalAddress(IPAddress Address, IPAddress? Broadcast, bool SupportsMulticast);

/// <summary>The computer's own network addresses, as discovery uses them.</summary>
public static class LocalNetwork
{
    /// <summary>IPv4 addresses of the interfaces that are up (no loopback, no tunnels).</summary>
    public static IReadOnlyList<LocalAddress> Addresses()
    {
        var result = new List<LocalAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(unicast.Address))
                {
                    continue;
                }

                result.Add(new LocalAddress(unicast.Address, unicast.IPv4Mask is { } mask ? BroadcastOf(unicast.Address, mask) : null, nic.SupportsMulticast));
            }
        }

        return result;
    }

    /// <summary>The addresses that can send multicast.</summary>
    public static IReadOnlyList<IPAddress> MulticastAddresses() => [.. Addresses().Where(a => a.SupportsMulticast).Select(a => a.Address)];

    public static IPAddress BroadcastOf(IPAddress address, IPAddress mask)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(mask);
        var bytes = address.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(bytes[i] | ~maskBytes[i]);
        }

        return new IPAddress(bytes);
    }
}

/// <summary>
/// The sockets a discovery query is sent from: one UDP socket per local IPv4 address, bound to that address and an
/// ephemeral port, open only while the query runs. Devices answer a multicast or broadcast query at the address and
/// port it came from, and Windows Firewall lets such answers in for a few seconds without an inbound rule ("unicast
/// response to multicast or broadcast"), so discovery never listens on a well-known port or on every interface and
/// Windows normally doesn't ask for network access (the UI still explains the first search beforehand).
/// </summary>
public sealed class QuerySockets : IDisposable
{
    private readonly List<Socket> _sockets = [];

    private QuerySockets()
    {
    }

    /// <summary>Where each socket is bound (the local address and its ephemeral port).</summary>
    public IReadOnlyList<IPEndPoint> LocalEndPoints => [.. _sockets.Select(s => (IPEndPoint)s.LocalEndPoint!)];

    /// <summary>Opens one socket per address; addresses that can't be bound (gone in the meantime) are skipped.</summary>
    /// <param name="multicastTtl">Multicast hops for the queries (null: no multicast).</param>
    /// <param name="localPort">
    /// The port to bind (0: an ephemeral one). For protocols whose devices answer at a fixed port rather than the
    /// query's (Govee: 4002); a port another program holds on an address skips that address.
    /// </param>
    public static QuerySockets Open(IEnumerable<IPAddress> locals, int? multicastTtl = null, bool broadcast = false, int localPort = 0)
    {
        ArgumentNullException.ThrowIfNull(locals);
        var sockets = new QuerySockets();
        foreach (var local in locals.Distinct())
        {
            var socket = new Socket(local.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.ExclusiveAddressUse = localPort != 0;
                socket.Bind(new IPEndPoint(local, localPort));
                socket.EnableBroadcast = broadcast;
                if (multicastTtl is { } ttl && local.AddressFamily == AddressFamily.InterNetwork)
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, ttl);
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                }

                sockets._sockets.Add(socket);
            }
            catch (SocketException)
            {
                socket.Dispose();
            }
        }

        return sockets;
    }

    /// <summary>Sends <paramref name="message"/> from every socket to the destination for its local address.</summary>
    /// <returns>How many sends succeeded.</returns>
    public int Send(byte[] message, Func<IPAddress, IPEndPoint?> destination)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(destination);
        var sent = 0;
        foreach (var socket in _sockets)
        {
            if (destination(((IPEndPoint)socket.LocalEndPoint!).Address) is not { } to)
            {
                continue;
            }

            try
            {
                socket.SendTo(message, to);
                sent++;
            }
            catch (SocketException)
            {
                // an interface that went down in the meantime is skipped
            }
        }

        return sent;
    }

    /// <summary>Hands every datagram any socket receives to <paramref name="received"/> until canceled or disposed.</summary>
    public Task ReceiveAsync(Action<byte[], IPEndPoint> received, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(received);
        return Task.WhenAll(_sockets.Select(s => ReceiveLoopAsync(s, received, cancellationToken)));
    }

    public void Dispose()
    {
        foreach (var socket in _sockets)
        {
            socket.Dispose();
        }
    }

    private static async Task ReceiveLoopAsync(Socket socket, Action<byte[], IPEndPoint> received, CancellationToken cancellationToken)
    {
        var buffer = new byte[9000];
        EndPoint any = new IPEndPoint(socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        while (!cancellationToken.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize)
            {
                continue;
            }
            catch (SocketException)
            {
                return;
            }

            received(buffer.AsSpan(0, result.ReceivedBytes).ToArray(), (IPEndPoint)result.RemoteEndPoint);
        }
    }
}
