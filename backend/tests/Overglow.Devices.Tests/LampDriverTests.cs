using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Overglow.Devices.Drivers.HomeAssistant;
using Overglow.Devices.Drivers.Lifx;
using Overglow.Devices.Drivers.Nanoleaf;
using Overglow.Devices.Drivers.Wiz;
using Overglow.Devices.Drivers.Zigbee2Mqtt;
using Overglow.Discovery;
using Overglow.Discovery.Mdns;

namespace Overglow.Devices.Tests;

/// <summary>WiZ, LIFX, Nanoleaf, Home Assistant and Zigbee2MQTT: messages byte-exact with the C++ drivers, flows against fakes.</summary>
public sealed class LampDriverTests
{
    private static string Text(byte[] datagram) => Encoding.UTF8.GetString(datagram);

    [Fact]
    public void Wiz_MessagesHaveSortedKeysLikeQt()
    {
        Assert.Equal("""{"method":"setPilot","params":{"b":3,"dimming":80,"g":2,"r":1}}""", WizDriver.SetPilot(1, 2, 3, 80));
        Assert.Equal("""{"method":"setPilot","params":{"dimming":80,"state":true}}""", WizDriver.Power(true, 80));
        Assert.Equal("""{"method":"setPilot","params":{"state":false}}""", WizDriver.Power(false, 80));
    }

    [Fact]
    public async Task Wiz_SendsOneDatagramPerLamp()
    {
        var channels = new ChannelFactory();
        var options = WizOptions.FromConfig(Ctx.Json("""
            { "dimming": 120, "lamps": [ { "name": "a", "ipAddress": "10.0.0.21" }, { "name": "b", "ipAddress": "not an ip" }, { "name": "c", "ipAddress": "10.0.0.22" } ] }
            """));
        using var driver = new WizDriver(options, Ctx.Create(channels: channels.Create));

        await driver.OpenAsync(3, TestContext.Current.CancellationToken);
        driver.Write([1, 2, 3, 4, 5, 6, 7, 8, 9]);
        await driver.CloseAsync(TestContext.Current.CancellationToken);

        Assert.Equal(100, options.Dimming);
        Assert.Equal(["10.0.0.21:38899", "10.0.0.22:38899"], channels.Channels.Select(c => c.Remote!.ToString()));
        Assert.Equal(
            ["""{"method":"setPilot","params":{"dimming":100,"state":true}}""", """{"method":"setPilot","params":{"b":3,"dimming":100,"g":2,"r":1}}""", """{"method":"setPilot","params":{"state":false}}"""],
            channels.Channels[0].Packets.Select(Text));
        Assert.Contains("\"r\":4", Text(channels.Channels[1].Packets.ElementAt(1)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wiz_DiscoveryListsEachBulbOnce()
    {
        var probe = new FakeProbe(
            new UdpProbeReply(new IPEndPoint(IPAddress.Parse("10.0.0.21"), 38899), Encoding.UTF8.GetBytes("""{"method":"getPilot","env":"pro","result":{"mac":"a8bb50112233","rssi":-60}}""")),
            new UdpProbeReply(new IPEndPoint(IPAddress.Parse("10.0.0.21"), 38899), Encoding.UTF8.GetBytes("""{"method":"getPilot","result":{"mac":"a8bb50112233"}}""")),
            new UdpProbeReply(new IPEndPoint(IPAddress.Parse("10.0.0.22"), 38899), Encoding.UTF8.GetBytes("not json")));

        var devices = await WizDriver.DiscoverAsync(Ctx.Request(Ctx.Create(discovery: Ctx.Discovery(probe: probe))));

        Assert.Equal("""{"method":"getPilot","params":{}}""", Text(Assert.Single(probe.Probes).Request));
        Assert.Equal("""[{"name":"Bulb #0","ipAddress":"10.0.0.21","macAddress":"a8bb50112233","value":"10.0.0.21"}]""", devices.ToJsonString());
    }

    private static readonly byte[] s_mac = [0xD0, 0x73, 0xD5, 0x01, 0x02, 0x03];

    private static byte[] LifxHeader(int size, ushort type) =>
        [(byte)size, 0, 0x00, 0x14, 0x78, 0x56, 0x34, 0x12, .. s_mac, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, (byte)type, (byte)(type >> 8), 0, 0];

    [Fact]
    public void Lifx_SetColorAndSetPowerPackets()
    {
        var packet = new byte[LifxProtocol.SetColorSize];
        LifxProtocol.WriteSetColor(packet, s_mac, new Vector3(0, 0, 1), 6500, 250);
        Assert.Equal([.. LifxHeader(49, 102), 0, 0xAA, 0xAA, 0xFF, 0xFF, 0xFF, 0xFF, 0x64, 0x19, 250, 0, 0, 0], packet);

        LifxProtocol.WriteSetPower(packet, s_mac, 0xFFFF);
        Assert.Equal([.. LifxHeader(38, 21), 0xFF, 0xFF], packet[..38]);

        Assert.Equal(
            [36, 0, 0x00, 0x34, 0x11, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, .. "LIFXV2"u8.ToArray(), 0x04, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0],
            LifxProtocol.GetService());
        Assert.Null(LifxProtocol.ParseMac("D0:73:D5:01:02"));
        Assert.Equal(s_mac, LifxProtocol.ParseMac("d0-73-d5-01-02-03"));
    }

    [Fact]
    public async Task Lifx_PowersOnStreamsFloatColorsAndPowersOff()
    {
        var channels = new ChannelFactory();
        var options = LifxOptions.FromConfig(Ctx.Json("""{ "transition": 100, "lamps": [ { "name": "a", "ipAddress": "10.0.0.31", "macAddress": "D0:73:D5:01:02:03" } ], "colorOrder": "bgr" }"""));
        using var driver = new LifxDriver(options, Ctx.Create(channels: channels.Create));

        await driver.OpenAsync(1, TestContext.Current.CancellationToken);
        driver.WriteFloat([new Vector3(1, 0, 0)], TimeSpan.Zero);
        await driver.CloseAsync(TestContext.Current.CancellationToken);

        var packets = Assert.Single(channels.Channels).Packets.ToArray();
        Assert.Equal("10.0.0.31:56700", channels.Channels[0].Remote!.ToString());
        Assert.Equal(new byte[] { 0xFF, 0xFF }, packets[0][36..]);
        Assert.Equal(new byte[] { 0, 0xAA, 0xAA }, packets[1][36..39]); // red in BGR order is blue
        Assert.Equal(new byte[] { 100, 0, 0, 0 }, packets[1][45..]);
        Assert.Equal(new byte[] { 0, 0 }, packets[2][36..]);
    }

    [Fact]
    public async Task Lifx_DiscoveryReadsStateServiceAnswers()
    {
        var answer = LifxHeader(41, 3);
        var probe = new FakeProbe(
            new UdpProbeReply(new IPEndPoint(IPAddress.Parse("10.0.0.31"), 56700), answer),
            new UdpProbeReply(new IPEndPoint(IPAddress.Parse("10.0.0.31"), 56700), answer),
            new UdpProbeReply(new IPEndPoint(IPAddress.Parse("10.0.0.32"), 56700), LifxHeader(36, 45)));

        var devices = await LifxDriver.DiscoverAsync(Ctx.Request(Ctx.Create(discovery: Ctx.Discovery(probe: probe))));

        Assert.Equal("""[{"name":"Lamp #0","ipAddress":"10.0.0.31","macAddress":"D0:73:D5:01:02:03","value":"10.0.0.31"}]""", devices.ToJsonString());
    }

    private static readonly JsonArray s_panels = JsonNode.Parse("""
        [ { "panelId": 1, "x": 0, "y": 0, "shapeType": 2 }, { "panelId": 2, "x": 100, "y": 0, "shapeType": 2 },
          { "panelId": 3, "x": 0, "y": 100, "shapeType": 2 }, { "panelId": 4, "x": 50, "y": 50, "shapeType": 1 } ]
        """)!.AsArray();

    [Fact]
    public void Nanoleaf_OrdersPanelsLikeTheCppDriver()
    {
        Assert.Equal(new ushort[] { 3, 1, 2 }, NanoleafDriver.OrderPanels(s_panels, topDown: true, leftRight: true));
        Assert.Equal(new ushort[] { 3, 2, 1 }, NanoleafDriver.OrderPanels(s_panels, topDown: true, leftRight: false));
        Assert.Equal(new ushort[] { 2, 1, 3 }, NanoleafDriver.OrderPanels(s_panels, topDown: false, leftRight: true));
    }

    [Fact]
    public async Task Nanoleaf_OpensExternalControlAndStreamsPanels()
    {
        var http = new FakeHttpHandler((request, _) => request.Method == HttpMethod.Get
            ? FakeHttpHandler.Json($$"""{ "name": "Shapes", "model": "NL42", "firmwareVersion": "9.0", "panelLayout": { "layout": { "numPanels": 4, "positionData": {{s_panels.ToJsonString()}} } } }""")
            : new HttpResponseMessage(HttpStatusCode.NoContent));
        var udp = new RecordingChannel();
        using var driver = new NanoleafDriver(NanoleafOptions.FromConfig(Ctx.Json("""{ "host": "10.0.0.9", "token": "TOKEN", "panelStartPos": 1 }""")), Contexts.With(http, udp));

        await driver.OpenAsync(2, TestContext.Current.CancellationToken);
        driver.Write([10, 20, 30, 40, 50, 60]);
        await driver.CloseAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
        [
            (HttpMethod.Get, "http://10.0.0.9:16021/api/v1/TOKEN/", null),
            (HttpMethod.Put, "http://10.0.0.9:16021/api/v1/TOKEN/effects", NanoleafDriver.ExternalControlV2),
            (HttpMethod.Put, "http://10.0.0.9:16021/api/v1/TOKEN/effects", NanoleafDriver.ExternalControlV2),
            (HttpMethod.Put, "http://10.0.0.9:16021/api/v1/TOKEN/state", """{"on":{"value":true}}"""),
            (HttpMethod.Put, "http://10.0.0.9:16021/api/v1/TOKEN/state", """{"on":{"value":false}}"""),
        ], http.Requests);
        Assert.Equal("192.0.2.10:60222", udp.Remote!.ToString());
        Assert.Equal(new byte[] { 0, 3, 0, 3, 0, 0, 0, 0, 0, 1, 0, 1, 10, 20, 30, 0, 0, 1, 0, 2, 40, 50, 60, 0, 0, 1 }, Assert.Single(udp.Packets));
        Assert.Null(driver.EffectiveRefreshInterval(TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task Nanoleaf_RefusesAStartPanelOutOfRange()
    {
        var http = new FakeHttpHandler((_, _) => FakeHttpHandler.Json($$"""{ "panelLayout": { "layout": { "positionData": {{s_panels.ToJsonString()}} } } }"""));
        using var driver = new NanoleafDriver(new NanoleafOptions("10.0.0.9", "T", StartPosition: 2), Contexts.With(http, new RecordingChannel()));

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await driver.OpenAsync(2, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void HomeAssistant_RequestBodiesHaveSortedKeysLikeQt()
    {
        var rgb = new LampEntity("light.desk", Hsv: false);
        var hs = new LampEntity("light.desk", Hsv: true);

        Assert.Equal("""{"brightness":200,"entity_id":"light.desk","rgb_color":[255,128,0],"transition":0.5}""", HomeAssistantDriver.ColorRequest(rgb, 255, 128, 0, 500, 200, out var luma));
        Assert.Equal(146, luma);
        Assert.Equal("""{"entity_id":"light.desk","hs_color":[0,100],"transition":1}""", HomeAssistantDriver.ColorRequest(hs, 255, 0, 0, 1000, null, out var lightness));
        Assert.Equal(128, lightness);
        Assert.Equal(new Uri("http://10.0.0.5:8123/"), new HomeAssistantOptions("10.0.0.5", "t", []).BaseUri);
        Assert.Equal(new Uri("https://ha.local:8443/"), new HomeAssistantOptions("https://ha.local:8443", "t", []).BaseUri);
    }

    [Fact]
    public async Task HomeAssistant_TurnsOnStreamsAndTurnsOff()
    {
        var authorization = new List<string?>();
        var http = new FakeHttpHandler((request, _) =>
        {
            authorization.Add(request.Headers.Authorization?.ToString());
            return FakeHttpHandler.Json("[]");
        });
        var options = HomeAssistantOptions.FromConfig(Ctx.Json("""
            { "homeAssistantHost": "10.0.0.5", "longLivedAccessToken": "TOKEN", "lamps": [ { "name": "light.desk", "colorModel": 0 } ] }
            """));
        using var driver = new HomeAssistantDriver(options, Ctx.Create(http: http));

        await driver.OpenAsync(1, TestContext.Current.CancellationToken);
        driver.Write([255, 128, 0]);
        driver.Write([0, 0, 0]);
        await driver.CloseAsync(TestContext.Current.CancellationToken);

        Assert.All(authorization, a => Assert.Equal("Bearer TOKEN", a));
        Assert.Equal(
        [
            (HttpMethod.Post, "http://10.0.0.5:8123/api/services/light/turn_on", """{"entity_id":["light.desk"]}"""),
            (HttpMethod.Post, "http://10.0.0.5:8123/api/services/light/turn_on", """{"entity_id":"light.desk","rgb_color":[255,128,0]}"""),
            (HttpMethod.Post, "http://10.0.0.5:8123/api/services/light/turn_on", """{"brightness":0,"entity_id":"light.desk","rgb_color":[0,0,0]}"""),
            (HttpMethod.Post, "http://10.0.0.5:8123/api/services/light/turn_off", """{"entity_id":["light.desk"]}"""),
        ], http.Requests);
    }

    [Fact]
    public async Task HomeAssistant_RestoresTheSavedState()
    {
        var http = new FakeHttpHandler((request, _) => request.Method == HttpMethod.Get
            ? FakeHttpHandler.Json("""{ "state": "on", "attributes": { "brightness": 77, "rgb_color": [1, 2, 3] } }""")
            : FakeHttpHandler.Json("[]"));
        var options = new HomeAssistantOptions("10.0.0.5", "T", [new LampEntity("light.a", false)], RestoreOriginalState: true);
        using var driver = new HomeAssistantDriver(options, Ctx.Create(http: http));

        await driver.OpenAsync(1, TestContext.Current.CancellationToken);
        await driver.CloseAsync(TestContext.Current.CancellationToken);

        Assert.Equal((HttpMethod.Get, "http://10.0.0.5:8123/api/states/light.a", (string?)null), http.Requests[0]);
        Assert.Equal((HttpMethod.Post, "http://10.0.0.5:8123/api/services/light/turn_on", """{"brightness":77,"entity_id":"light.a","rgb_color":[1,2,3]}"""), http.Requests[^1]);
    }

    [Fact]
    public async Task HomeAssistant_DiscoveryUsesMdns()
    {
        var mdns = new FakeMdns(new MdnsServiceInstance(KnownServices.HomeAssistant, "Home._home-assistant._tcp.local.", "homeassistant.local", IPAddress.Parse("10.0.0.5"), 8123, []));

        var devices = await HomeAssistantDriver.DiscoverAsync(Ctx.Request(Ctx.Create(discovery: Ctx.Discovery(mdns: mdns))));

        Assert.Equal("""[{"value":"10.0.0.5:8123","name":"10.0.0.5:8123 (homeassistant.local)"}]""", devices.ToJsonString());
    }

    [Fact]
    public void Zigbee2Mqtt_MessagesHaveSortedKeysLikeQt()
    {
        Assert.Equal("""{"color":{"b":3,"g":2,"r":1}}""", Zigbee2MqttDriver.ColorMessage(new LampEntity("a", false), 1, 2, 3, 0, null, out _));
        Assert.Equal("""{"brightness":128,"color":{"hue":120,"saturation":100},"transition":0.25}""", Zigbee2MqttDriver.ColorMessage(new LampEntity("a", true), 0, 255, 0, 250, 128, out var b));
        Assert.Equal(128, b);
    }

    [Fact]
    public async Task Zigbee2Mqtt_PublishesPerLampAndSwitchesOffOnShutdown()
    {
        var mqtt = new FakeMqtt();
        var options = Zigbee2MqttOptions.FromConfig(Ctx.Json("""{ "constantBrightness": 0, "lamps": [ { "name": "Desk", "colorModel": 0 }, { "name": "Shelf", "colorModel": 1 } ] }"""));
        using var driver = new Zigbee2MqttDriver(options, Ctx.Create(mqtt: mqtt));

        await driver.OpenAsync(2, TestContext.Current.CancellationToken);
        var shutdown = Assert.Single(mqtt.Shutdown).Value;
        driver.Write([255, 0, 0, 0, 0, 255]);
        await driver.CloseAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
        [
            ("zigbee2mqtt/Desk/set", """{"state":"ON"}"""),
            ("zigbee2mqtt/Shelf/set", """{"state":"ON"}"""),
            ("zigbee2mqtt/Desk/set", """{"brightness":54,"color":{"b":0,"g":0,"r":255}}"""),
            ("zigbee2mqtt/Shelf/set", """{"brightness":128,"color":{"hue":240,"saturation":100}}"""),
            ("zigbee2mqtt/Desk/set", """{"state":"OFF"}"""),
            ("zigbee2mqtt/Shelf/set", """{"state":"OFF"}"""),
        ], mqtt.Published);
        Assert.Equal([new("zigbee2mqtt/Desk/set", """{"state":"OFF"}"""), new KeyValuePair<string, string>("zigbee2mqtt/Shelf/set", """{"state":"OFF"}""")], shutdown);
        Assert.Empty(mqtt.Shutdown);
    }

    [Fact]
    public async Task Zigbee2Mqtt_DiscoveryReadsTheBridgeDeviceList()
    {
        var mqtt = new FakeMqtt();
        mqtt.Retained[Zigbee2MqttDriver.DevicesTopic] = """
            [ { "friendly_name": "Coordinator" },
              { "friendly_name": "Desk", "definition": { "exposes": [ { "type": "light", "features": [ { "name": "state" }, { "name": "color_xy" } ] } ] } },
              { "friendly_name": "Bulb", "definition": { "exposes": [ { "type": "light", "features": [ { "name": "color_hs" } ] } ] } },
              { "friendly_name": "Plug", "definition": { "exposes": [ { "type": "switch", "features": [ { "name": "color_xy" } ] } ] } } ]
            """;

        var devices = await Zigbee2MqttDriver.DiscoverAsync(Ctx.Request(Ctx.Create(mqtt: mqtt)));

        Assert.Equal("""[{"value":"RGB","name":"Desk"},{"value":"HSV","name":"Bulb"}]""", devices.ToJsonString());
    }

    [Fact]
    public void Zigbee2Mqtt_NeedsTheMqttConnection() =>
        Assert.Throws<ArgumentException>(() => new Zigbee2MqttDriver(new Zigbee2MqttOptions([new LampEntity("a", false)]), Ctx.Create()));
}
