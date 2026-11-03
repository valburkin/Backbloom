using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Backbloom.Devices.Drivers.Govee;
using Backbloom.Discovery;

namespace Backbloom.Devices.Tests;

/// <summary>The Govee LAN driver: messages, rate limit, discovery and identify (recorded datagrams, no network).</summary>
public sealed class GoveeTests
{
    private const string TwoLamps = """
        { "type": "govee", "lamps": [
            { "name": "Left", "ipAddress": "192.0.2.21", "deviceId": "1F:80:C5:32:32:36:72:4E", "sku": "H6199" },
            { "name": "Right", "ipAddress": "192.0.2.22" },
            { "name": "Broken", "ipAddress": "not an address" } ],
          "brightness": 80 }
        """;

    private static string Text(byte[] datagram) => Encoding.UTF8.GetString(datagram);

    [Fact]
    public void Messages()
    {
        var packet = new byte[GoveeProtocol.MaxColorLength];
        var n = GoveeProtocol.WriteColor(packet, 255, 0, 7);
        Assert.Equal("""{"msg":{"cmd":"colorwc","data":{"color":{"r":255,"g":0,"b":7},"colorTemInKelvin":0}}}""", Encoding.UTF8.GetString(packet, 0, n));
        Assert.True(n <= GoveeProtocol.MaxColorLength);
        Assert.Equal("""{"msg":{"cmd":"turn","data":{"value":1}}}""", Text(GoveeProtocol.Turn(true)));
        Assert.Equal("""{"msg":{"cmd":"turn","data":{"value":0}}}""", Text(GoveeProtocol.Turn(false)));
        Assert.Equal("""{"msg":{"cmd":"brightness","data":{"value":100}}}""", Text(GoveeProtocol.Brightness(150)));
        Assert.Equal("""{"msg":{"cmd":"scan","data":{"account_topic":"reserve"}}}""", Text(GoveeProtocol.Scan));
        Assert.NotNull(JsonNode.Parse(Encoding.UTF8.GetString(packet, 0, n))!["msg"]!["data"]!["color"]);
    }

    [Fact]
    public async Task Stream_OnWithBrightnessThenChangedColorsAtMostEveryInterval()
    {
        var channels = new ChannelFactory();
        var time = new FakeTimeProvider();
        var context = Ctx.Create(channels: channels.Create);
        var driver = new GoveeDriver(GoveeOptions.FromConfig(Ctx.Json(TwoLamps)) with { CommandSpacing = TimeSpan.Zero }, new DriverContext(context.LoggerFactory, context.Http, channels.Create) { Time = time });
        Assert.Equal(TimeSpan.FromMilliseconds(100), driver.EffectiveRefreshInterval(null));

        await driver.OpenAsync(2, CancellationToken.None);
        Assert.Equal(2, channels.Channels.Count);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.0.2.21"), 4003), channels.Channels[0].Remote);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.0.2.22"), 4003), channels.Channels[1].Remote);
        Assert.Equal(
            ["""{"msg":{"cmd":"turn","data":{"value":1}}}""", """{"msg":{"cmd":"brightness","data":{"value":80}}}"""],
            channels.Channels[0].Packets.Select(Text));

        Assert.True(driver.Write([10, 20, 30, 40, 50, 60]));
        Assert.Contains("\"r\":10,\"g\":20,\"b\":30", Text(channels.Channels[0].Packets.Last()), StringComparison.Ordinal);
        Assert.Contains("\"r\":40,\"g\":50,\"b\":60", Text(channels.Channels[1].Packets.Last()), StringComparison.Ordinal);

        // too soon: nothing goes out; after the interval only the light whose color changed
        Assert.True(driver.Write([1, 2, 3, 40, 50, 60]));
        Assert.Equal(3, channels.Channels[0].Packets.Count);
        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True(driver.Write([1, 2, 3, 40, 50, 60]));
        Assert.Equal(4, channels.Channels[0].Packets.Count);
        Assert.Equal(3, channels.Channels[1].Packets.Count);

        // every 2 s every light gets its color again (a datagram may have been lost)
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(driver.Write([1, 2, 3, 40, 50, 60]));
        Assert.Equal(4, channels.Channels[1].Packets.Count);

        await driver.CloseAsync(CancellationToken.None);
        Assert.Equal("""{"msg":{"cmd":"turn","data":{"value":0}}}""", Text(channels.Channels[1].Packets.Last()));
        Assert.False(driver.Write([0, 0, 0, 0, 0, 0]));
        driver.Dispose();
        Assert.All(channels.Channels, c => Assert.True(c.Disposed));
    }

    [Fact]
    public async Task Write_DoesntAllocate()
    {
        var channel = new NullChannel();
        var time = new FakeTimeProvider();
        var context = new DriverContext(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, new HttpClient(), () => channel) { Time = time };
        using var driver = new GoveeDriver(GoveeOptions.FromConfig(Ctx.Json(TwoLamps)) with { CommandSpacing = TimeSpan.Zero }, context);
        await driver.OpenAsync(2, CancellationToken.None);
        byte[] colors = [1, 2, 3, 4, 5, 6];
        driver.Write(colors);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 200; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(150));
            colors[0] = (byte)i;
            driver.Write(colors);
        }

        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    [Fact]
    public void NoLamps_IsAConfigurationError() =>
        Assert.Throws<ArgumentException>(() => new GoveeDriver(GoveeOptions.FromConfig(Ctx.Json("""{ "lamps": [] }""")), Ctx.Create()));

    [Fact]
    public async Task Discover_ScansFromPort4002AndListsEachLightOnce()
    {
        static UdpProbeReply Reply(string ip, string json) => new(new IPEndPoint(IPAddress.Parse(ip), 4001), Encoding.UTF8.GetBytes(json));
        var probe = new FakeMulticast(
            Reply("192.0.2.21", """{"msg":{"cmd":"scan","data":{"ip":"192.0.2.21","device":"1F:80:C5:32:32:36:72:4E","sku":"H6199","bleVersionHard":"3.01.01"}}}"""),
            Reply("192.0.2.21", """{"msg":{"cmd":"scan","data":{"ip":"192.0.2.21","device":"1F:80:C5:32:32:36:72:4E","sku":"H6199"}}}"""),
            Reply("192.0.2.23", """{"msg":{"cmd":"scan","data":{"device":"AA:BB:CC:DD:EE:FF:00:11","sku":""}}}"""),
            Reply("192.0.2.24", """{"msg":{"cmd":"devStatus","data":{}}}"""),
            Reply("192.0.2.26", """{"msg":{"cmd":"scan","data":{"ip":"192.0.2.26","sku":"H6008"}}}"""),
            Reply("192.0.2.26", """{"msg":{"cmd":"scan","data":{"ip":"192.0.2.26","sku":"H6008"}}}"""),
            Reply("192.0.2.25", "not json"));
        var context = Ctx.Create(discovery: Ctx.Discovery() with { Multicast = probe });

        var lights = await GoveeDriver.DiscoverAsync(Ctx.Request(context));

        var (request, group, localPort) = Assert.Single(probe.Probes);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("239.255.255.250"), 4001), group);
        Assert.Equal(4002, localPort);
        Assert.Equal(GoveeProtocol.Scan, request);
        Assert.Equal(3, lights.Count);
        Assert.Equal("""{"name":"Govee H6199 (192.0.2.21)","ipAddress":"192.0.2.21","deviceId":"1F:80:C5:32:32:36:72:4E","sku":"H6199","value":"192.0.2.21"}""", lights[0]!.ToJsonString());
        Assert.Equal("192.0.2.23", lights[1]!["ipAddress"]!.GetValue<string>());
        Assert.Equal("Govee light #1", lights[1]!["name"]!.GetValue<string>());

        // a light that reports no device id is listed once, by its address
        Assert.Equal("192.0.2.26", lights[2]!["ipAddress"]!.GetValue<string>());
        Assert.Equal(string.Empty, lights[2]!["deviceId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Identify_BlinksTheLight_AndLeavesAnUnknownOneOff()
    {
        var channels = new ChannelFactory();
        var context = Ctx.Create(channels: channels.Create);
        await GoveeDriver.IdentifyAsync(Ctx.Request(context, """{ "ipAddress": "192.0.2.21" }"""));
        var channel = Assert.Single(channels.Channels);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.0.2.21"), 4003), channel.Remote);
        Assert.Equal(6, channel.Packets.Count);
        Assert.Equal("""{"msg":{"cmd":"turn","data":{"value":0}}}""", Text(channel.Packets.Last()));
        Assert.True(channel.Disposed);

        // the state was asked for at the light's control port, the answer awaited at port 4002
        var (request, to, localPort) = Assert.Single(((FakeMulticast)context.Discovery.Multicast).Probes);
        Assert.Equal(GoveeProtocol.DevStatus, request);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.0.2.21"), 4003), to);
        Assert.Equal(4002, localPort);

        // no address: nothing is sent
        await GoveeDriver.IdentifyAsync(Ctx.Request(context, "{}"));
        Assert.Single(channels.Channels);
    }

    [Fact]
    public async Task Identify_RestoresTheLightAsItWas()
    {
        var channels = new ChannelFactory();
        var status = new FakeMulticast(new UdpProbeReply(
            new IPEndPoint(IPAddress.Parse("192.0.2.21"), 4003),
            Encoding.UTF8.GetBytes("""{"msg":{"cmd":"devStatus","data":{"onOff":1,"brightness":35,"color":{"r":255,"g":120,"b":0},"colorTemInKelvin":0}}}""")));
        var context = Ctx.Create(channels: channels.Create, discovery: Ctx.Discovery() with { Multicast = status });
        await GoveeDriver.IdentifyAsync(Ctx.Request(context, """{ "ipAddress": "192.0.2.21" }"""));
        var sent = Assert.Single(channels.Channels).Packets.Select(Text).ToList();
        Assert.Equal(9, sent.Count);
        Assert.Equal(
            [
                """{"msg":{"cmd":"turn","data":{"value":1}}}""",
                """{"msg":{"cmd":"brightness","data":{"value":35}}}""",
                """{"msg":{"cmd":"colorwc","data":{"color":{"r":255,"g":120,"b":0},"colorTemInKelvin":0}}}""",
            ],
            sent[^3..]);

        Assert.Equal(new GoveeStatus(true, 35, (255, 120, 0), 0), GoveeProtocol.ReadStatus(Encoding.UTF8.GetBytes("""{"msg":{"cmd":"devStatus","data":{"onOff":1,"brightness":35,"color":{"r":255,"g":120,"b":0},"colorTemInKelvin":0}}}""")));
        Assert.Null(GoveeProtocol.ReadStatus(Encoding.UTF8.GetBytes("""{"msg":{"cmd":"scan","data":{}}}""")));
        Assert.Equal("""{"msg":{"cmd":"colorwc","data":{"color":{"r":0,"g":0,"b":0},"colorTemInKelvin":4000}}}""", Text(GoveeProtocol.ColorTemperature(4000)));
    }
    [Fact]
    public void Registered_WithSettingTiers()
    {
        Assert.True(new DriverRegistry().RegisterAll().TryGet("govee", out var govee));
        Assert.Equal("leds_group_2_network", govee.Group);
        var properties = govee.Schema["properties"]!.AsObject();
        Assert.Equal("advanced", properties["brightness"]!["access"]!.GetValue<string>());
        Assert.Equal("expert", properties["updateInterval"]!["access"]!.GetValue<string>());
        Assert.Null(properties["lamps"]!["access"]);
    }

    private sealed class NullChannel : Transport.IDatagramChannel
    {
        public void Connect(IPEndPoint remote)
        {
        }

        public bool Send(ReadOnlySpan<byte> datagram) => true;

        public void Dispose()
        {
        }
    }
}
