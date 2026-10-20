using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Overglow.App;
using Overglow.Discovery.Mdns;
using Overglow.Server;

namespace Overglow.Shell.Tests;

/// <summary>
/// Where the server listens: 127.0.0.1 only for every copy (installed or not) until the user turns remote access on;
/// then the network endpoint is bound live and unbound again when it is turned off. The tests stand in 127.0.0.2 for
/// "every interface", so nothing listens on the network (see also Shared/NetworkListenerGuard).
/// </summary>
public sealed class ListenAddressTests
{
    private const string RemoteAccessOn = """
        { "network": { "remoteAccess": true, "apiAuth": true, "internetAccessAPI": false, "restirctedInternetAccessAPI": false,
                       "ipWhitelist": [], "localApiAuth": false, "localAdminAuth": true } }
        """;

    private static readonly IPAddress s_network = IPAddress.Parse("127.0.0.2");

    [Fact]
    public void Default_IsLoopbackOnly()
    {
        var options = AppOptions.FromConfiguration(new ConfigurationBuilder().Build());
        Assert.Null(options.BindAddress);
        Assert.Equal("http://127.0.0.1:18290/", AppUrls.Ui(options).AbsoluteUri);
    }

    [Theory]
    [InlineData("bind", "0.0.0.0")]
    [InlineData("BIND", "192.168.1.20")]
    public void Option_OrEnvironment_Overrides(string key, string value)
    {
        var options = AppOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build());
        Assert.Equal(IPAddress.Parse(value), options.BindAddress);
    }

    [Fact]
    public async Task TestHost_ListensOnLoopbackOnly()
    {
        await using var host = await TestHost.StartAsync();
        AssertLoopbackOnly(host);
    }

    [Fact]
    public async Task DefaultHost_ListensOnLoopbackOnly_AndAdvertisesNothing()
    {
        var responder = new FakeMdnsTransport();
        await using var host = await StartAsync(responder);

        AssertLoopbackOnly(host);
        var status = host.App.Services.GetRequiredService<INetworkAccess>().Status;
        Assert.Equal((false, false, false), (status.Enabled, status.FixedByCommandLine, status.Advertised));
        Assert.Empty(status.Urls);
        Assert.Equal(0, responder.Starts);
        Assert.False(RemoteAccess.IsEnabledIn(host.Settings.GetSection("network")));
    }

    [Fact]
    public async Task RemoteAccess_BindsAndUnbindsLive_WhileLoopbackKeepsWorking()
    {
        var responder = new FakeMdnsTransport();
        await using var host = await StartAsync(responder);
        var port = Port(host);
        var network = host.App.Services.GetRequiredService<INetworkAccess>();
        using var http = new HttpClient();
        Assert.False(await AcceptsAsync(s_network, port));

        using var loopback = new TcpClient();
        await loopback.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);

        SetRemoteAccess(host, true);
        Wait.Until(() => AcceptsAsync(s_network, port).GetAwaiter().GetResult(), 10000);
        Assert.True(network.Status.Enabled);
        Assert.True(network.Status.Advertised);
        Assert.Equal(port, network.Status.Port);
        Assert.Equal([$"http://127.0.0.2:{port}/"], network.Status.Urls);
        Assert.Equal(1, responder.Starts);
        Assert.True(await RpcAsync(http, IPAddress.Loopback, port));
        Assert.True(await RpcAsync(http, s_network, port));
        Assert.True(loopback.Connected);

        SetRemoteAccess(host, false);
        Wait.Until(() => !AcceptsAsync(s_network, port).GetAwaiter().GetResult(), 10000);
        Assert.False(network.Status.Enabled);
        Assert.False(network.Status.Advertised);
        Assert.Equal(1, responder.Disposals);
        Assert.True(await RpcAsync(http, IPAddress.Loopback, port));
        AssertLoopbackOnly(host);
    }

    [Fact]
    public async Task BindOption_FixesTheAddress_TheSettingHasNoEffect()
    {
        await using var host = await TestHost.StartAsync(RemoteAccessOn, configure: (_, s) => s.AddSingleton(new RemoteAccessOptions(() => [s_network])));
        var status = host.App.Services.GetRequiredService<INetworkAccess>().Status;

        Assert.True(status.FixedByCommandLine);
        Assert.False(status.Enabled);
        Assert.False(await AcceptsAsync(s_network, Port(host)));
        AssertLoopbackOnly(host);
    }

    private static Task<TestHost> StartAsync(FakeMdnsTransport responder) =>
        TestHost.StartAsync(extraArgs: [], configure: (_, services) =>
        {
            services.AddSingleton(new RemoteAccessOptions(() => [s_network]));
            services.AddSingleton(_ => new MdnsService(() => responder, () => new FakeMdnsTransport(), NullLogger<MdnsService>.Instance));
        });

    private static void SetRemoteAccess(TestHost host, bool on)
    {
        var network = host.Settings.GetSection("network")!.AsObject();
        network["remoteAccess"] = on;
        Assert.True(host.Settings.Update(new JsonObject { ["network"] = network }).Success);
    }

    private static int Port(TestHost host) =>
        new Uri(host.App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;

    private static void AssertLoopbackOnly(TestHost host)
    {
        var addresses = host.App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        Assert.NotEmpty(addresses);
        Assert.All(addresses, a => Assert.Equal(IPAddress.Loopback, IPAddress.Parse(new Uri(a).Host.Trim('[', ']'))));
    }

    private static async Task<bool> AcceptsAsync(IPAddress address, int port)
    {
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(address, port).WaitAsync(TimeSpan.FromSeconds(2));
            return true;
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException)
        {
            return false;
        }
    }

    private static async Task<bool> RpcAsync(HttpClient http, IPAddress address, int port)
    {
        using var content = new StringContent("""{ "command": "sysinfo", "tan": 1 }""");
        using var response = await http.PostAsync(new Uri($"http://{address}:{port}/json-rpc"), content, TestContext.Current.CancellationToken);
        return response.IsSuccessStatusCode;
    }
}

/// <summary>An mDNS transport that sends nothing (the responder of tests that turn remote access on).</summary>
internal sealed class FakeMdnsTransport : IMdnsTransport
{
    public bool CanAdvertise => true;

    public int Starts { get; private set; }

    public int Disposals { get; private set; }

    public void Start(Action<byte[], IPEndPoint> received) => Starts++;

    public void SendMulticast(byte[] message)
    {
    }

    public void SendUnicast(byte[] message, IPEndPoint destination)
    {
    }

    public void Dispose() => Disposals++;
}
