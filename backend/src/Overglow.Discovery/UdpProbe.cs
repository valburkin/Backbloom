using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Overglow.Discovery;

/// <summary>An answer to a broadcast probe.</summary>
public sealed record UdpProbeReply(IPEndPoint From, byte[] Data);

/// <summary>Broadcasts a discovery request (WiZ "getPilot", LIFX "GetService") and collects the answers.</summary>
public interface IUdpProbe
{
    Task<IReadOnlyList<UdpProbeReply>> ProbeAsync(byte[] request, int port, TimeSpan duration, CancellationToken cancellationToken);
}

/// <summary>
/// Sends the request to the broadcast address of every IPv4 interface (as the C++ drivers did with
/// QNetworkInterface::allInterfaces) from one socket and returns every datagram received within the duration.
/// </summary>
/// <param name="targets">Where to send for a port; the interfaces' broadcast addresses by default (tests use loopback).</param>
public sealed class UdpProbe(Func<int, IEnumerable<IPEndPoint>>? targets = null) : IUdpProbe
{
    private readonly Func<int, IEnumerable<IPEndPoint>> _targets = targets ?? BroadcastTargets;

    public async Task<IReadOnlyList<UdpProbeReply>> ProbeAsync(byte[] request, int port, TimeSpan duration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { EnableBroadcast = true };
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        foreach (var target in _targets(port))
        {
            try
            {
                await socket.SendToAsync(request, SocketFlags.None, target, cancellationToken);
            }
            catch (SocketException)
            {
                // an interface that can't broadcast (e.g. down in the meantime) is skipped
            }
        }

        var replies = new List<UdpProbeReply>();
        var buffer = new byte[4096];
        using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        window.CancelAfter(duration);
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (true)
        {
            try
            {
                var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, window.Token);
                replies.Add(new UdpProbeReply((IPEndPoint)result.RemoteEndPoint, buffer.AsSpan(0, result.ReceivedBytes).ToArray()));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return replies;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
            }
        }
    }

    /// <summary>The directed broadcast address of every IPv4 interface that is up.</summary>
    public static IEnumerable<IPEndPoint> BroadcastTargets(int port)
    {
        var seen = new HashSet<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null)
                {
                    continue;
                }

                var address = unicast.Address.GetAddressBytes();
                var mask = unicast.IPv4Mask.GetAddressBytes();
                for (var i = 0; i < 4; i++)
                {
                    address[i] = (byte)(address[i] | ~mask[i]);
                }

                var broadcast = new IPAddress(address);
                if (seen.Add(broadcast))
                {
                    yield return new IPEndPoint(broadcast, port);
                }
            }
        }
    }
}
