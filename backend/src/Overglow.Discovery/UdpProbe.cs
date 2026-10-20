using System.Net;

namespace Overglow.Discovery;

/// <summary>An answer to a broadcast probe.</summary>
public sealed record UdpProbeReply(IPEndPoint From, byte[] Data);

/// <summary>Broadcasts a discovery request (WiZ "getPilot", LIFX "GetService") and collects the answers.</summary>
public interface IUdpProbe
{
    Task<IReadOnlyList<UdpProbeReply>> ProbeAsync(byte[] request, int port, TimeSpan duration, CancellationToken cancellationToken);
}

/// <summary>A probe target: the local address to send from and where to send to.</summary>
public sealed record UdpProbeTarget(IPAddress Local, IPEndPoint Target);

/// <summary>
/// Sends the request to the broadcast address of every IPv4 interface (as the C++ drivers did with
/// QNetworkInterface::allInterfaces), each from an ephemeral socket bound to that interface's address
/// (<see cref="QuerySockets"/>), and returns every datagram received within the duration.
/// </summary>
/// <param name="targets">Where to send for a port; the interfaces' broadcast addresses by default (tests use loopback).</param>
public sealed class UdpProbe(Func<int, IEnumerable<UdpProbeTarget>>? targets = null) : IUdpProbe
{
    private readonly Func<int, IEnumerable<UdpProbeTarget>> _targets = targets ?? BroadcastTargets;

    public async Task<IReadOnlyList<UdpProbeReply>> ProbeAsync(byte[] request, int port, TimeSpan duration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var targetList = _targets(port).ToList();
        using var sockets = QuerySockets.Open(targetList.Select(t => t.Local), broadcast: true);
        sockets.Send(request, local => targetList.FirstOrDefault(t => t.Local.Equals(local))?.Target);

        var replies = new List<UdpProbeReply>();
        using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        window.CancelAfter(duration);
        await sockets.ReceiveAsync(
            (datagram, sender) =>
            {
                lock (replies)
                {
                    replies.Add(new UdpProbeReply(sender, datagram));
                }
            },
            window.Token);
        cancellationToken.ThrowIfCancellationRequested();
        return replies;
    }

    /// <summary>The directed broadcast address of every IPv4 interface that is up, sent from that interface's address.</summary>
    public static IEnumerable<UdpProbeTarget> BroadcastTargets(int port) =>
        LocalNetwork.Addresses()
            .Where(a => a.Broadcast is not null)
            .DistinctBy(a => a.Broadcast)
            .Select(a => new UdpProbeTarget(a.Address, new IPEndPoint(a.Broadcast!, port)));
}
