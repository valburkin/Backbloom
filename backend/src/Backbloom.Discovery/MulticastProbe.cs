using System.Net;

namespace Backbloom.Discovery;

/// <summary>
/// Sends a discovery request to a multicast group from a fixed local port and collects the answers devices send to
/// that port (Govee's LAN API: query to 239.255.255.250:4001, answers to port 4002 of the querier's address).
/// </summary>
public interface IMulticastProbe
{
    Task<IReadOnlyList<UdpProbeReply>> ProbeAsync(byte[] request, IPEndPoint group, int localPort, TimeSpan duration, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IMulticastProbe"/> over <see cref="QuerySockets"/>: one socket per local IPv4 address that can send
/// multicast, bound to that address and <c>localPort</c> (never to every address), open only while the probe runs; the
/// query goes out from that socket, so the answers arrive as replies to our own query (Windows Firewall lets those in
/// for a few seconds without a rule, as for the other searches).
/// </summary>
/// <param name="locals">The addresses to send from; the multicast-capable interface addresses by default (tests use loopback).</param>
public sealed class MulticastProbe(Func<IEnumerable<IPAddress>>? locals = null) : IMulticastProbe
{
    private readonly Func<IEnumerable<IPAddress>> _locals = locals ?? LocalNetwork.MulticastAddresses;

    public async Task<IReadOnlyList<UdpProbeReply>> ProbeAsync(byte[] request, IPEndPoint group, int localPort, TimeSpan duration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(group);
        using var sockets = QuerySockets.Open(_locals(), multicastTtl: 1, localPort: localPort);
        sockets.Send(request, _ => group);

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
}
