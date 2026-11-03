using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Backbloom.Devices;
using Backbloom.Devices.Setup;
using Backbloom.Discovery;
using Backbloom.Discovery.Mdns;
using Backbloom.Discovery.Ssdp;
using Backbloom.Discovery.Usb;
using Backbloom.Engine;
using Backbloom.Engine.Platform;
using Backbloom.Tests.Fakes;

namespace Backbloom.Server.Tests;

/// <summary>
/// The <c>hardware</c> command end to end: fake boards on fake USB ports, a fake WLED over HTTP on 127.0.0.1 and a
/// fake monitor list. Nothing real is opened or contacted.
/// </summary>
public sealed class HardwareRpcTests
{
    private static readonly ImprovTimeouts s_fast = new(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));

    private static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();

    private static bool Success(JsonObject reply) => reply["success"]!.GetValue<bool>();

    private static string? Error(JsonObject reply) => reply["error"]?.GetValue<string>();

    private static SerialPortInfo Esp(string port) => new(port, $@"\\.\{port}", "USB-SERIAL CH340", 0x1a86, 0x7523);

    /// <summary>
    /// A server whose drivers and USB step see only fake serial ports (COM3 and COM4, both ESP boards) and fake
    /// discovery (nothing is sent to the network), with host names resolved by <paramref name="hosts"/> only (a name it
    /// lacks can't be found), the guided setup's WLED client allowed to reach the fakes on 127.0.0.1, a monitor list of
    /// its own and the log lines in <paramref name="log"/>.
    /// </summary>
    private static Task<TestServer> StartAsync(FakeImprovPorts ports, string? settings = null, IReadOnlyDictionary<string, IPAddress>? hosts = null, MemoryLog? log = null, FakeWifi? wifi = null, IMdnsBrowser? mdns = null) =>
        TestServer.StartAsync(settings, configure: (_, services) =>
        {
            ValueTask<IPAddress> Resolve(string name, CancellationToken cancellationToken) =>
                IPAddress.TryParse(name, out var literal) ? ValueTask.FromResult(literal)
                : hosts?.GetValueOrDefault(name) is { } address ? ValueTask.FromResult(address)
                : ValueTask.FromException<IPAddress>(new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound));
            services.AddSingleton(sp => new DriverContext(sp.GetRequiredService<ILoggerFactory>(), sp.GetRequiredService<HttpClient>(), null, Resolve)
            {
                SerialPorts = ports,
                Discovery = new DiscoveryServices(mdns ?? new NoMdns(), new NoSsdp(), new Enumerator(Esp("COM3"), Esp("COM4")), new NoHid(), new NoProbe()) { Multicast = new NoMulticast() },
            });
            services.AddSingleton(sp => new UsbBoards(sp.GetRequiredService<DriverContext>(), s_fast));
            services.AddSingleton(sp => new WledSetup(sp.GetRequiredService<DriverContext>(), TimeSpan.Zero, a => IPAddress.IsLoopback(a) || WledHosts.IsLanAddress(a), applyWait: TimeSpan.Zero));
            services.AddSingleton<IDisplayList>(new Displays());
            var fakeWifi = wifi ?? new FakeWifi { State = WifiState.None };
            services.AddSingleton<IWifi>(fakeWifi);
            services.AddSingleton(sp => new BoardFinder(sp.GetRequiredService<DriverContext>(), sp.GetRequiredService<UsbBoards>(), sp.GetRequiredService<WledSetup>(), fakeWifi, new BoardFinderTimes(TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)), fakeWifi.LocalAddresses));
            if (log is not null)
            {
                services.AddSingleton<ILoggerProvider>(log);
            }
        });
    [Fact]
    public async Task Screens_ReportTheMonitorSizes()
    {
        await using var server = await StartAsync(new FakeImprovPorts());
        await using var client = await server.ConnectAsync();
        var reply = await client.CallAsync("hardware", "screens");
        Assert.True(Success(reply), Error(reply));
        Assert.Equal(
            """[{"name":"DELL U2720Q","primary":true,"widthMm":597,"heightMm":336,"device":"\\\\.\\DISPLAY1","devicePath":"path-1"},{"name":"Projector","primary":false,"widthMm":null,"heightMm":null,"device":"\\\\.\\DISPLAY2","devicePath":""}]""",
            reply["info"]!["screens"]!.ToJsonString());
        Assert.Equal("hardware-screens", reply["command"]!.GetValue<string>());
    }

    [Fact]
    public async Task Usb_ScanNetworksAndWifi_SkippingThePortADeviceUses()
    {
        var ports = new FakeImprovPorts();
        var board = ports.Add("COM3");
        board.Networks.AddRange([("Home", -55, true), ("Guest", -80, false)]);
        ports.Add("COM4");
        var log = new MemoryLog();
        await using var server = await StartAsync(ports, """{ "device": { "type": "adalight", "output": "COM4", "rate": 115200 }, "logger": { "level": "debug" } }""", log: log);
        await using var client = await server.ConnectAsync();

        var scan = await client.CallAsync("hardware", "usb-scan");
        Assert.True(Success(scan), Error(scan));
        Assert.Equal(
            """[{"port":"COM3","name":"USB-SERIAL CH340","state":"ready","firmware":"WLED","version":"0.15.0","chip":"esp32","deviceName":"WLED","address":null}]""",
            scan["info"]!["boards"]!.ToJsonString());
        Assert.Empty(scan["info"]!["silent"]!.AsArray());
        Assert.Equal((false, false), board.SignalsAtOpen);

        var networks = await client.CallAsync("hardware", "usb-networks", Obj("""{ "port": "COM3" }"""));
        Assert.True(Success(networks), Error(networks));
        Assert.Equal("""[{"ssid":"Home","signal":90,"secure":true,"rssi":-55},{"ssid":"Guest","signal":40,"secure":false,"rssi":-80}]""", networks["info"]!["networks"]!.ToJsonString());

        var busy = await client.CallAsync("hardware", "usb-networks", Obj("""{ "port": "COM4" }"""));
        Assert.Equal("COM4 is used by one of your devices", Error(busy));

        var wrong = await client.CallAsync("hardware", "usb-wifi", Obj("""{ "port": "COM3", "ssid": "Home", "password": "nope" }"""));
        Assert.Equal("couldn’t join Home: check the password", Error(wrong));
        Assert.Equal("hardware-usb-wifi", wrong["command"]!.GetValue<string>());

        var joined = await client.CallAsync("hardware", "usb-wifi", Obj("""{ "port": "COM3", "ssid": "Home", "password": "right-password" }"""));
        Assert.True(Success(joined), Error(joined));
        Assert.Equal("""{"address":"http://192.0.2.50","host":"192.0.2.50"}""", joined["info"]!.ToJsonString());

        Assert.Equal("port is required", Error(await client.CallAsync("hardware", "usb-networks")));
        Assert.Equal("Unknown or missing subcommand", Error(await client.CallAsync("hardware", "usb-flash")));

        // the password went to the board and nowhere else: the log names the network, never the password
        Assert.Contains(log.Lines, line => line.Contains("sending the Wi-Fi settings for Home", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Lines, line => line.Contains("right-password", StringComparison.Ordinal) || line.Contains("nope", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Usb_JoiningDoesntHoldUpTheConnection()
    {
        var ports = new FakeImprovPorts();
        using var gate = new ManualResetEventSlim();
        var board = ports.Add("COM3");
        board.JoinGate = gate;
        await using var server = await StartAsync(ports);
        await using var client = await server.ConnectAsync();

        // the board joins only when the gate opens: meanwhile the connection answers other requests
        var joining = client.CallAsync("hardware", "usb-wifi", Obj("""{ "port": "COM3", "ssid": " Home ", "password": "right-password" }"""));
        var info = await client.CallAsync("sysinfo");
        Assert.True(Success(info));
        Assert.False(joining.IsCompleted);
        gate.Set();
        Assert.True(Success(await joining));

        // the network name went to the board as typed, spaces and all
        Assert.Equal(" Home ", board.JoinedSsid);
    }

    [Fact]
    public async Task Wled_ReadWriteAndChecks()
    {
        var board = new FakeWledBoard();
        await using var wled = await FakeHttpServer.StartAsync(board);
        await using var server = await StartAsync(new FakeImprovPorts());
        await using var client = await server.ConnectAsync();
        var host = wled.Host;

        var read = await client.CallAsync("hardware", "wled-read", new JsonObject { ["host"] = host });
        Assert.True(Success(read), Error(read));
        Assert.Equal(30, read["info"]!["ledCount"]!.GetValue<int>());
        Assert.Equal("ws2812b", read["info"]!["outputs"]![0]!["strip"]!.GetValue<string>());

        var write = await client.CallAsync("hardware", "wled-write", Obj($$"""
            { "host": "{{host}}", "outputs": [{ "index": 0, "strip": "ws2812b", "length": 45, "colorOrder": "RGB" }], "maxCurrent": 1700, "ledCurrent": 55 }
            """));
        Assert.True(Success(write), Error(write));
        Assert.False(write["info"]!["restarting"]!.GetValue<bool>());
        Assert.Equal(45, board.Cfg["hw"]!["led"]!["ins"]![0]!["len"]!.GetValue<int>());
        Assert.Equal(1700, board.Cfg["hw"]!["led"]!["maxpwr"]!.GetValue<int>());

        var wifi = await client.CallAsync("hardware", "wled-wifi", Obj($$"""{ "host": "{{host}}", "ssid": "Home", "password": "secret" }"""));
        Assert.True(Success(wifi), Error(wifi));
        Assert.True(wifi["info"]!["restarting"]!.GetValue<bool>());
        Assert.Equal(1, board.Restarts);
        Assert.Equal("Home", board.Cfg["nw"]!["ins"]![0]!["ssid"]!.GetValue<string>());

        var first = await client.CallAsync("hardware", "wled-test", Obj($$"""{ "host": "{{host}}", "output": 0, "pattern": "first" }"""));
        Assert.True(Success(first), Error(first));
        Assert.Equal("{}", first["info"]!.ToJsonString());
        var color = await client.CallAsync("hardware", "wled-test", Obj($$"""{ "host": "{{host}}", "output": 0, "pattern": "color", "color": [0, 0, 255] }"""));
        Assert.True(Success(color), Error(color));
        Assert.Equal("""[0,45,"000000",0,45,"0000FF"]""", board.State["seg"]![0]!["i"]!.ToJsonString());
        Assert.True(Success(await client.CallAsync("hardware", "wled-test", Obj($$"""{ "host": "{{host}}", "pattern": "off" }"""))));
        Assert.Equal(9, board.State["seg"]![0]!["fx"]!.GetValue<int>());

        Assert.Equal("pattern must be first, range, color, white, points or off", Error(await client.CallAsync("hardware", "wled-test", Obj($$"""{ "host": "{{host}}", "pattern": "rainbow" }"""))));
        Assert.Equal("leds must be a list of LED numbers", Error(await client.CallAsync("hardware", "wled-test", Obj($$"""{ "host": "{{host}}", "pattern": "points", "leds": ["a"] }"""))));
        var points = await client.CallAsync("hardware", "wled-test", Obj($$"""{ "host": "{{host}}", "pattern": "points", "leds": [1, 5] }"""));
        Assert.True(Success(points), Error(points));
        Assert.EndsWith(""","A0A0A0",0,"FFFFFF",4,"FFFFFF"]""", board.State["seg"]![0]!["i"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal("color must be [r, g, b] with values 0 to 255", Error(await client.CallAsync("hardware", "wled-test", Obj($$"""{ "host": "{{host}}", "pattern": "color", "color": [300, 0, 0] }"""))));
        Assert.Equal("outputs[0]: unknown strip type 'neon'", Error(await client.CallAsync("hardware", "wled-write", Obj($$"""{ "host": "{{host}}", "outputs": [{ "strip": "neon", "length": 3 }] }"""))));
        Assert.Equal("host is required", Error(await client.CallAsync("hardware", "wled-read")));

        // over HTTP the reply comes in the response
        using var http = new HttpClient();
        using var response = await http.PostAsync(new Uri(server.BaseUri, "json-rpc"), new StringContent($$"""{ "command": "hardware", "subcommand": "wled-read", "host": "{{host}}", "tan": 5 }""", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        var reply = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!.AsObject();
        Assert.True(Success(reply), Error(reply));
        Assert.Equal(5, reply["tan"]!.GetValue<int>());
        Assert.Equal(45, reply["info"]!["outputs"]![0]!["length"]!.GetValue<int>());
    }

    [Fact]
    public async Task Wled_RefusesABoardARunningDeviceStreamsTo()
    {
        var board = new FakeWledBoard();
        await using var wled = await FakeHttpServer.StartAsync(board);
        using var sink = new DdpSink();
        await using var server = await StartAsync(new FakeImprovPorts(), sink.Device("127.0.0.1"));
        await using var client = await server.ConnectAsync();
        await WaitUntilRunningAsync(server);

        var write = await client.CallAsync("hardware", "wled-write", Obj($$"""{ "host": "{{wled.Host}}", "name": "Desk" }"""));
        Assert.StartsWith("Backbloom is sending colors to this board", Error(write), StringComparison.Ordinal);
        var test = await client.CallAsync("hardware", "wled-test", Obj($$"""{ "host": "{{wled.Host}}", "pattern": "first" }"""));
        Assert.StartsWith("Backbloom is sending colors to this board", Error(test), StringComparison.Ordinal);
        Assert.Empty(board.Requests);

        // reading is fine
        Assert.True(Success(await client.CallAsync("hardware", "wled-read", Obj($$"""{ "host": "{{wled.Host}}" }"""))));
    }

    /// <summary>Add lights for Home Assistant: its lights and a flash, against a fake Home Assistant on 127.0.0.1.</summary>
    [Fact]
    public async Task HomeAssistant_LightsAndIdentify()
    {
        var calls = new List<(string Method, string Path, string? Body)>();
        await using var ha = await FakeHttpServer.StartAsync((method, path, authorization, body) =>
        {
            lock (calls)
            {
                calls.Add((method, path, body));
            }

            return authorization != "Bearer good" ? (HttpStatusCode.Unauthorized, "401: Unauthorized")
                : (method, path) switch
                {
                    ("GET", "/api/states") => (HttpStatusCode.OK, """[{"entity_id":"light.tv","attributes":{"friendly_name":"TV backlight"}},{"entity_id":"sensor.t","attributes":{}}]"""),
                    ("POST", "/api/services/light/turn_on") => (HttpStatusCode.OK, "[]"),
                    _ => (HttpStatusCode.NotFound, "{}"),
                };
        });
        await using var server = await TestServer.StartAsync();
        await using var client = await server.ConnectAsync();

        var lights = await client.CallAsync("leddevice", "getProperties", Obj($$"""{ "ledDeviceType": "home_assistant", "params": { "host": "{{ha.Host}}", "token": "good" } }"""));
        Assert.True(Success(lights), Error(lights));
        Assert.Equal("""{"lights":[{"id":"light.tv","name":"TV backlight"}]}""", lights["info"]!["properties"]!.ToJsonString());

        var refused = await client.CallAsync("leddevice", "getProperties", Obj($$"""{ "ledDeviceType": "home_assistant", "params": { "host": "{{ha.Host}}", "token": "bad" } }"""));
        Assert.StartsWith("Home Assistant refused the access token", refused["info"]!["properties"]!["error"]!.GetValue<string>(), StringComparison.Ordinal);

        var flash = await client.CallAsync("leddevice", "identify", Obj($$"""{ "ledDeviceType": "home_assistant", "params": { "host": "{{ha.Host}}", "token": "good", "entityId": "light.tv" } }"""));
        Assert.True(Success(flash), Error(flash));
        Assert.Contains(calls, c => c is { Method: "POST", Path: "/api/services/light/turn_on", Body: """{"entity_id":"light.tv","flash":"short"}""" });

        var failed = await client.CallAsync("leddevice", "identify", Obj($$"""{ "ledDeviceType": "home_assistant", "params": { "host": "{{ha.Host}}", "token": "bad", "entityId": "light.tv" } }"""));
        Assert.Equal("identify failed for home_assistant: Home Assistant answered HTTP 401", Error(failed));
    }

    [Fact]
    public async Task Wled_RefusesABoardADeviceReachesByName()
    {
        var board = new FakeWledBoard();
        await using var wled = await FakeHttpServer.StartAsync(board);
        var hosts = new Dictionary<string, IPAddress> { ["desk-wled"] = IPAddress.Loopback };
        using var sink = new DdpSink();
        await using var server = await StartAsync(new FakeImprovPorts(), sink.Device("desk-wled"), hosts);
        await using var client = await server.ConnectAsync();
        await WaitUntilRunningAsync(server);

        var write = await client.CallAsync("hardware", "wled-write", Obj($$"""{ "host": "{{wled.Host}}", "name": "Desk" }"""));
        Assert.Equal("Backbloom is sending colors to this board (the device \"Device\"): turn that device off first", Error(write)?.Replace(DeviceName(server), "Device", StringComparison.Ordinal));
        Assert.Empty(board.Requests);
    }

    [Fact]
    public async Task Wled_RefusesWhenAStreamingDevicesAddressCantBeFound()
    {
        var board = new FakeWledBoard();
        await using var wled = await FakeHttpServer.StartAsync(board);
        var hosts = new System.Collections.Concurrent.ConcurrentDictionary<string, IPAddress> { ["desk-wled"] = IPAddress.Loopback };
        using var sink = new DdpSink();
        await using var server = await StartAsync(new FakeImprovPorts(), sink.Device("desk-wled"), hosts);
        await using var client = await server.ConnectAsync();
        await WaitUntilRunningAsync(server);

        // streaming to an address nobody can name any more (not even its last one): it may be this board
        hosts.TryRemove("desk-wled", out _);
        var test = await client.CallAsync("hardware", "wled-test", Obj($$"""{ "host": "{{wled.Host}}", "pattern": "first" }"""));
        Assert.StartsWith("Backbloom may be sending colors to this board", Error(test), StringComparison.Ordinal);
        Assert.Contains("desk-wled", Error(test), StringComparison.Ordinal);
        Assert.Empty(board.Requests);
    }

    [Fact]
    public async Task Wled_IgnoresDevicesThatDontStreamToABoard()
    {
        // a running device that isn't a strip type (here the test device) doesn't block the wizard, whatever its host
        var board = new FakeWledBoard();
        await using var wled = await FakeHttpServer.StartAsync(board);
        await using var server = await StartAsync(new FakeImprovPorts(), """{ "device": { "type": "file", "output": "\\\\.\\NUL", "host": "gone-wled" } }""");
        await using var client = await server.ConnectAsync();
        await WaitUntilRunningAsync(server);

        var test = await client.CallAsync("hardware", "wled-test", Obj($$"""{ "host": "{{wled.Host}}", "pattern": "first" }"""));
        Assert.True(Success(test), Error(test));
    }
    [Fact]
    public async Task Usb_SkipsThePortAnAutoSerialDeviceWouldPick()
    {
        var ports = new FakeImprovPorts();
        ports.Add("COM3");
        ports.Add("COM4");

        // "auto" without the ESP handshake takes the first port that isn't Bluetooth: COM3
        await using var server = await StartAsync(ports, """{ "device": { "type": "adalight", "output": "auto", "rate": 115200 } }""");
        await using var client = await server.ConnectAsync();
        var scan = await client.CallAsync("hardware", "usb-scan");
        Assert.True(Success(scan), Error(scan));
        Assert.Equal(["COM4"], scan["info"]!["boards"]!.AsArray().Select(b => b!["port"]!.GetValue<string>()));
        Assert.Equal("COM3 is used by one of your devices", Error(await client.CallAsync("hardware", "usb-networks", Obj("""{ "port": "COM3" }"""))));
    }

    [Fact]
    public async Task Wled_RefusesHostsOffTheNetwork()
    {
        await using var server = await StartAsync(new FakeImprovPorts(), hosts: new Dictionary<string, IPAddress> { ["example.com"] = IPAddress.Parse("93.184.216.34") });
        await using var client = await server.ConnectAsync();
        Assert.Equal("example.com isn’t on your local network: only boards there can be set up", Error(await client.CallAsync("hardware", "wled-read", Obj("""{ "host": "example.com" }"""))));
        Assert.Equal("8.8.8.8 isn’t on your local network: only boards there can be set up", Error(await client.CallAsync("hardware", "wled-read", Obj("""{ "host": "8.8.8.8" }"""))));
        Assert.Equal("Couldn’t find wled-nowhere on your network", Error(await client.CallAsync("hardware", "wled-read", Obj("""{ "host": "wled-nowhere" }"""))));
    }

    private static string DeviceName(TestServer server) => server.Services.GetRequiredService<LightingEngine>().Setup.Controllers[0].Name;

    private static async Task WaitUntilRunningAsync(TestServer server)
    {
        var engine = server.Services.GetRequiredService<LightingEngine>();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!engine.Devices.Any(d => d.State == DeviceState.Ready))
        {
            Assert.True(DateTime.UtcNow < deadline, "the device didn't start");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }
    /// <summary>The one search: USB, the network (a fake WLED announced on 127.0.0.1) and hotspots, merged.</summary>
    [Fact]
    public async Task Find_EveryRouteAtOnce()
    {
        var board = new FakeWledBoard();
        board.Info["name"] = "Desk";
        await using var wled = await FakeHttpServer.StartAsync(board);
        var port = int.Parse(wled.Host.Split(':')[1], System.Globalization.CultureInfo.InvariantCulture);
        var mdns = new OneWled(new MdnsServiceInstance(KnownServices.Wled, "wled-desk", "wled-desk.local", IPAddress.Loopback, port, []));
        var ports = new FakeImprovPorts();
        ports.Add("COM3");
        var wifi = new FakeWifi();
        wifi.Networks.AddRange([new("Home", 90, true), new("WLED-AP", 60, true)]);
        await using var server = await StartAsync(ports, wifi: wifi, mdns: mdns);
        await using var client = await server.ConnectAsync();

        var found = await client.CallAsync("hardware", "find");
        Assert.True(Success(found), Error(found));
        var boards = found["info"]!["boards"]!.AsArray();
        Assert.Equal(["usb:COM3", "network:A0:B1:C2:D3:E4:F5", "hotspot:WLED-AP"], boards.Select(b => b!["id"]!.GetValue<string>()));
        Assert.Equal(
            $$"""{"id":"network:A0:B1:C2:D3:E4:F5","via":"network","name":"Desk","isNew":false,"port":null,"host":"127.0.0.1:{{port}}","hotspot":null,"mac":"A0:B1:C2:D3:E4:F5","version":"0.15.0","ledCount":30,"setUp":true,"portName":null,"firmware":null,"chip":null,"deviceName":null,"signal":null,"mdnsName":"wled-desk"}""",
            boards[1]!.ToJsonString());
        Assert.Equal("""{"id":"hotspot:WLED-AP","via":"hotspot","name":"WLED-AP","isNew":true,"port":null,"host":null,"hotspot":"WLED-AP","mac":null,"version":null,"ledCount":null,"setUp":null,"portName":null,"firmware":null,"chip":null,"deviceName":null,"signal":60,"mdnsName":null}""", boards[2]!.ToJsonString());
        Assert.Equal("""{"adapter":true,"currentSsid":"Home","blocked":null,"savedPassword":false}""", found["info"]!["wifi"]!.ToJsonString());
        Assert.Equal(["COM4"], found["info"]!["usbSilent"]!.AsArray().Select(p => p!["port"]!.GetValue<string>()));
        Assert.Empty(wifi.Joins);
    }

    [Fact]
    public async Task ConnectWifi_OverUsb_WithProgress()
    {
        var ports = new FakeImprovPorts();
        ports.Add("COM3").Url = "http://192.168.1.80";
        await using var server = await StartAsync(ports);
        await using var client = await server.ConnectAsync();
        var found = await client.CallAsync("hardware", "find");
        Assert.Equal("""{"adapter":false,"currentSsid":null,"blocked":null,"savedPassword":false}""", found["info"]!["wifi"]!.ToJsonString());

        var joined = await client.CallAsync("hardware", "connect-wifi", Obj("""{ "id": "usb:COM3", "ssid": "Home", "password": "right-password" }"""));
        Assert.True(Success(joined), Error(joined));
        Assert.Equal("""{"host":"192.168.1.80","address":"http://192.168.1.80","name":"WLED","mac":null}""", joined["info"]!.ToJsonString());
        var progress = client.NextEvent("hardware-progress-update");
        Assert.Equal("sending", progress["data"]!["step"]!.GetValue<string>());
        Assert.Equal("usb:COM3", progress["data"]!["id"]!.GetValue<string>());

        // a hotspot without Wi-Fi on this PC, and an id that isn't a board
        Assert.Equal("This PC has no Wi-Fi: plug the board into this PC with a USB cable instead", Error(await client.CallAsync("hardware", "connect-wifi", Obj("""{ "id": "hotspot:WLED-AP", "ssid": "Home", "password": "x" }"""))));
        Assert.Equal("Search again: that board isn’t there any more", Error(await client.CallAsync("hardware", "connect-wifi", Obj("""{ "id": "nonsense", "ssid": "Home", "password": "x" }"""))));
        Assert.Equal("ssid is required", Error(await client.CallAsync("hardware", "connect-wifi", Obj("""{ "id": "usb:COM3" }"""))));
    }

    [Fact]
    public async Task Wled_IdentifyAndProbe()
    {
        var board = new FakeWledBoard();
        await using var wled = await FakeHttpServer.StartAsync(board);
        await using var server = await StartAsync(new FakeImprovPorts());
        await using var client = await server.ConnectAsync();
        var host = wled.Host;

        var identify = await client.CallAsync("hardware", "wled-identify", Obj($$"""{ "host": "{{host}}" }"""));
        Assert.True(Success(identify), Error(identify));
        Assert.Equal(7, board.PostedStates.Count);

        var probe = await client.CallAsync("hardware", "wled-probe-start", Obj($$"""{ "host": "{{host}}", "round": 1 }"""));
        Assert.True(Success(probe), Error(probe));
        var candidates = probe["info"]!["candidates"]!.AsArray();
        Assert.Equal(8, candidates.Count);
        Assert.Equal("""{"output":0,"pin":16}""", candidates[0]!.ToJsonString());
        Assert.Equal("""{"output":1,"pin":2}""", candidates[1]!.ToJsonString());

        // 2000 LEDs over the 8 candidates: the first 20 of each white, dim, the rest black
        Assert.Equal(250, board.Cfg["hw"]!["led"]!["ins"]![0]!["len"]!.GetValue<int>());
        Assert.StartsWith("""[0,2000,"000000",0,20,"FFFFFF",250,270,"FFFFFF",""", board.State["seg"]![0]!["i"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(WledSetup.LowBrightness, board.State["bri"]!.GetValue<int>());

        // one candidate at a time, dim white over the whole output: the others go black
        var one = await client.CallAsync("hardware", "wled-test", Obj($$"""{ "host": "{{host}}", "output": 3, "pattern": "color", "color": [255, 255, 255] }"""));
        Assert.True(Success(one), Error(one));
        Assert.Equal("""[0,2000,"000000",750,1000,"FFFFFF"]""", board.State["seg"]![0]!["i"]!.ToJsonString());
        Assert.Equal(WledSetup.LowBrightness, board.State["bri"]!.GetValue<int>());

        var end = await client.CallAsync("hardware", "wled-probe-end", Obj($$"""{ "host": "{{host}}", "keep": false }"""));
        Assert.True(end["info"]!["restarting"]!.GetValue<bool>());
        Assert.Equal(30, board.Cfg["hw"]!["led"]!["ins"]![0]!["len"]!.GetValue<int>());
        Assert.Equal("round must be 1 or 2", Error(await client.CallAsync("hardware", "wled-probe-start", Obj($$"""{ "host": "{{host}}", "round": 3 }"""))));
    }

    private sealed class OneWled(MdnsServiceInstance instance) : IMdnsBrowser
    {
        public Task<IReadOnlyList<MdnsServiceInstance>> BrowseAsync(string serviceType, TimeSpan wait, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MdnsServiceInstance>>(serviceType == instance.ServiceType ? [instance] : []);

        public Task<IPAddress?> ResolveAsync(string hostName, TimeSpan wait, CancellationToken cancellationToken) => Task.FromResult<IPAddress?>(null);
    }
    [Fact]
    public async Task OnlyAdmins()
    {
        await using var server = await TestServer.StartAsync(password: TestServer.Password);
        await using var client = await server.ConnectFromNetworkAsync();
        Assert.Equal("No Authorization", Error(await client.CallAsync("hardware", "screens")));
    }

    private sealed class NoMdns : IMdnsBrowser
    {
        public Task<IReadOnlyList<MdnsServiceInstance>> BrowseAsync(string serviceType, TimeSpan wait, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MdnsServiceInstance>>([]);

        public Task<IPAddress?> ResolveAsync(string hostName, TimeSpan wait, CancellationToken cancellationToken) => Task.FromResult<IPAddress?>(null);
    }

    private sealed class NoSsdp : ISsdpSearcher
    {
        public Task<IReadOnlyList<SsdpResponse>> SearchAsync(string searchTarget, int port, TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SsdpResponse>>([]);
    }

    private sealed class NoHid : IHidEnumerator
    {
        public IReadOnlyList<HidDeviceInfo> GetDevices() => [];
    }

    private sealed class NoProbe : IUdpProbe
    {
        public Task<IReadOnlyList<UdpProbeReply>> ProbeAsync(byte[] request, int port, TimeSpan duration, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<UdpProbeReply>>([]);
    }

    private sealed class NoMulticast : IMulticastProbe
    {
        public Task<IReadOnlyList<UdpProbeReply>> ProbeAsync(byte[] request, IPEndPoint group, int localPort, TimeSpan duration, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<UdpProbeReply>>([]);
    }

    private sealed class Enumerator(params SerialPortInfo[] ports) : ISerialPortEnumerator
    {
        public IReadOnlyList<SerialPortInfo> GetPorts() => ports;
    }

    private sealed class Displays : IDisplayList
    {
        public IReadOnlyList<DisplayInfo> GetDisplays() =>
        [
            new("DELL U2720Q", @"\\.\DISPLAY1", "path-1", true, 597, 336),
            new("Projector", @"\\.\DISPLAY2", string.Empty, false, null, null),
        ];
    }
}

/// <summary>A fake HTTP API (a <see cref="FakeWledBoard"/>, a Home Assistant) served on 127.0.0.1 (a random port).</summary>
internal sealed class FakeHttpServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private FakeHttpServer(WebApplication app, string host)
    {
        _app = app;
        Host = host;
    }

    /// <summary>"127.0.0.1:&lt;port&gt;", as the requests name the board.</summary>
    public string Host { get; }

    public static Task<FakeHttpServer> StartAsync(FakeWledBoard board) =>
        StartAsync((method, path, _, body) => board.Handle(method, path, body));

    /// <summary>Any fake HTTP API on 127.0.0.1: the handler gets the method, path, Authorization header and body.</summary>
    public static async Task<FakeHttpServer> StartAsync(Func<string, string, string, string?, (HttpStatusCode Status, string Body)> handle)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.Run(async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(context.RequestAborted);
            var (status, reply) = handle(context.Request.Method, context.Request.Path.Value ?? "/", context.Request.Headers.Authorization.ToString(), body.Length == 0 ? null : body);
            context.Response.StatusCode = (int)status;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(reply, context.RequestAborted);
        });
        await app.StartAsync();
        var address = new Uri(app.Urls.First());
        return new FakeHttpServer(app, $"127.0.0.1:{address.Port}");
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>Every log line of the server, at every level its settings let through.</summary>
internal sealed class MemoryLog : ILoggerProvider
{
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
            {
                return [.. _lines];
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose()
    {
    }

    private sealed class Logger(MemoryLog log) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (log._lines)
            {
                log._lines.Add($"{formatter(state, exception)} {exception}");
            }
        }
    }
}

/// <summary>A UDP socket on 127.0.0.1 that a DDP device streams to (and nothing reads), so the device runs.</summary>
internal sealed class DdpSink : IDisposable
{
    private readonly System.Net.Sockets.UdpClient _socket = new(new IPEndPoint(IPAddress.Loopback, 0));

    public int Port => ((IPEndPoint)_socket.Client.LocalEndPoint!).Port;

    /// <summary>The <c>device</c> settings of a DDP device sending here under the name <paramref name="host"/>.</summary>
    public string Device(string host) => $$"""{ "device": { "type": "ddp", "host": "{{host}}", "port": {{Port}} } }""";

    public void Dispose() => _socket.Dispose();
}
