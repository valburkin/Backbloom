using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backbloom.Devices.Tests;

public sealed class HostResolverTests
{
    private static readonly IPAddress s_wled = IPAddress.Parse("192.0.2.40");

    private static Task<IPAddress[]> NotFound(string host, CancellationToken cancellationToken) =>
        throw new SocketException((int)SocketError.HostNotFound);

    [Fact]
    public async Task UsesTheSystemResolver_PreferringIPv4_AndPassesAddressesThrough()
    {
        var mdns = new FakeMdns();
        var resolver = new HostResolver(() => mdns, NullLogger<HostResolver>.Instance,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("fe80::1"), s_wled }));

        Assert.Equal(s_wled, await resolver.ResolveAsync("wled-desk.local", TestContext.Current.CancellationToken));
        Assert.Equal(IPAddress.Parse("192.0.2.7"), await resolver.ResolveAsync("192.0.2.7", TestContext.Current.CancellationToken));
        Assert.Empty(mdns.Resolved);
    }

    [Fact]
    public async Task AsksWithMdns_WhenTheSystemCantResolveALocalName()
    {
        var mdns = new FakeMdns { Hosts = { ["wled-desk.local"] = s_wled } };
        var resolver = new HostResolver(() => mdns, NullLogger<HostResolver>.Instance, NotFound);

        Assert.Equal(s_wled, await resolver.ResolveAsync("wled-desk.local", TestContext.Current.CancellationToken));
        Assert.Equal(["wled-desk.local"], mdns.Resolved);

        // other names aren't mDNS names
        await Assert.ThrowsAsync<SocketException>(() => resolver.ResolveAsync("wled.example", TestContext.Current.CancellationToken).AsTask());
        Assert.Single(mdns.Resolved);
    }

    [Fact]
    public async Task FallsBackToTheLastAddress_WhenNothingResolvesTheName()
    {
        var mdns = new FakeMdns();
        var systemWorks = true;
        var resolver = new HostResolver(() => mdns, NullLogger<HostResolver>.Instance,
            (host, ct) => systemWorks ? Task.FromResult(new[] { s_wled }) : NotFound(host, ct));

        Assert.Equal(s_wled, await resolver.ResolveAsync("wled-desk.local", TestContext.Current.CancellationToken));

        systemWorks = false;
        Assert.Equal(s_wled, await resolver.ResolveAsync("WLED-desk.local", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<SocketException>(() => resolver.ResolveAsync("wled-other.local", TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(["WLED-desk.local", "wled-other.local"], mdns.Resolved);
    }

    [Fact]
    public async Task GivesTheSystemResolverLimitedTime_ThenGoesOn()
    {
        var mdns = new FakeMdns { Hosts = { ["wled-desk.local"] = s_wled } };
        var resolver = new HostResolver(() => mdns, NullLogger<HostResolver>.Instance,
            async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return [];
            },
            TimeSpan.FromMilliseconds(50));

        Assert.Equal(s_wled, await resolver.ResolveAsync("wled-desk.local", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void DriverContext_ResolvesWithAHostResolverByDefault()
    {
        var context = new DriverContext(NullLoggerFactory.Instance, new HttpClient()) { Discovery = Ctx.Discovery() };

        Assert.Same(context.ResolveHost, context.ResolveHost);
        Assert.IsType<HostResolver>(context.ResolveHost.Target);
    }
}
