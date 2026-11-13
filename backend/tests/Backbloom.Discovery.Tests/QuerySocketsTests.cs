using System.Net;
using System.Net.Sockets;

namespace Backbloom.Discovery.Tests;

/// <summary>The query sockets on loopback, and the invariants of the computer's address list.</summary>
public sealed class QuerySocketsTests
{
    [Fact]
    public void Open_SkipsAddressesThisComputerDoesntHave()
    {
        // 192.0.2.1 (TEST-NET-1, documentation only) is on no interface: binding it fails, as for an interface gone
        using var sockets = QuerySockets.Open([IPAddress.Parse("192.0.2.1"), IPAddress.Loopback]);

        Assert.Equal(IPAddress.Loopback, Assert.Single(sockets.LocalEndPoints).Address);
    }

    [Fact]
    public async Task Send_GoesWhereADestinationIsKnown_AndCountsOnlyTheSendsThatWorked()
    {
        using var peer = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        peer.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var peerEndPoint = (IPEndPoint)peer.LocalEndPoint!;
        using var sockets = QuerySockets.Open([IPAddress.Loopback, IPAddress.Parse("127.0.0.2")]);

        // only the 127.0.0.1 socket has somewhere to send to
        Assert.Equal(1, sockets.Send("hello"u8.ToArray(), local => local.Equals(IPAddress.Loopback) ? peerEndPoint : null));
        var buffer = new byte[16];
        var received = await peer.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), TestContext.Current.CancellationToken);
        Assert.Equal(sockets.LocalEndPoints[0], received.RemoteEndPoint);

        // a datagram larger than UDP can carry fails on both sockets, quietly
        Assert.Equal(0, sockets.Send(new byte[70_000], _ => peerEndPoint));
    }

    [Fact]
    public void LocalNetwork_ListsTheComputersOwnIpv4Addresses_WithoutLoopback()
    {
        var addresses = LocalNetwork.Addresses();

        Assert.All(addresses, a =>
        {
            Assert.Equal(AddressFamily.InterNetwork, a.Address.AddressFamily);
            Assert.False(IPAddress.IsLoopback(a.Address));
        });
        Assert.All(LocalNetwork.MulticastAddresses(), a => Assert.Contains(addresses, l => l.Address.Equals(a) && l.SupportsMulticast));
        var targets = UdpProbe.BroadcastTargets(38899).ToList();
        Assert.All(targets, t => Assert.Equal(38899, t.Target.Port));
        Assert.Equal(targets.Count, targets.Select(t => t.Target.Address).Distinct().Count());
    }
}
