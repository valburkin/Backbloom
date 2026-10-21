using System.Text.Json.Nodes;

namespace Backbloom.Server.Tests;

/// <summary>The protocol of one layout driving several controllers, and old single-controller clients next to it.</summary>
public sealed class MultiControllerRpcTests
{
    private const string NoSmoothing = """{ "smoothing": { "enable": false } }""";

    private static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();

    private static bool Success(JsonObject reply) => reply["success"]!.GetValue<bool>();

    private static string? Error(JsonObject reply) => reply["error"]?.GetValue<string>();

    // "tv" drives the owner's 91 + 77 LEDs, "desk" 10 more; both write to the null device.
    private static JsonObject Layout() => Obj("""
        { "version": 1,
          "room": { "width": 4, "depth": 4, "height": 2.6, "screen": { "x": 1.7, "y": 1.31, "width": 0.6, "height": 0.34 },
                    "furniture": "desk" },
          "controllers": [
            { "id": "tv", "name": "TV", "kind": "wled", "host": "tv.local",
              "outputs": [{ "index": 0, "start": 0, "length": 91 }, { "index": 1, "start": 91, "length": 77 }],
              "device": { "type": "file", "output": "\\\\.\\NUL", "printTimeStamp": false } },
            { "id": "desk", "name": "Desk", "outputs": [{ "index": 0, "start": 0, "length": 10 }],
              "device": { "type": "file", "output": "\\\\.\\NUL", "printTimeStamp": false } } ],
          "strips": [
            { "id": "monitor", "name": "Monitor", "controllerId": "tv", "output": 0, "ledCount": 91, "depth": 0.2,
              "path": [{ "surface": "front", "x": 2.3, "y": 1.65 }, { "surface": "front", "x": 1.7, "y": 1.65 }] },
            { "id": "ceiling", "name": "Ceiling", "controllerId": "tv", "output": 1, "ledCount": 77, "depth": 0.25,
              "path": [{ "surface": "ceiling", "x": 3.8, "y": 3.8 }, { "surface": "ceiling", "x": 0.2, "y": 3.8 }] },
            { "id": "desk-strip", "name": "Desk strip", "controllerId": "desk", "output": 0, "ledCount": 10, "brightness": 0.5,
              "path": [{ "surface": "left", "x": 3.5, "y": 1.7 }, { "surface": "left", "x": 0.5, "y": 1.7 }] } ] }
        """);

    [Fact]
    public async Task SetConfig_Layout_DrivesEveryControllerAndReportsThem()
    {
        await using var server = await TestServer.StartAsync(NoSmoothing);
        await using var client = await server.ConnectAsync();
        await client.CallAsync("serverinfo", parameters: Obj("""{ "subscribe": ["leds-update", "controllers-update", "settings-update"] }"""));

        var saved = await client.CallAsync("config", "setconfig", new JsonObject { ["config"] = new JsonObject { ["layout"] = Layout() } });
        Assert.True(Success(saved), Error(saved));
        Assert.Equal(178, client.NextEvent("leds-update")["data"]!["leds"]!.AsArray().Count);
        client.NextEvent("controllers-update", e => e["data"]!["controllers"]!.AsArray().Count == 2);

        var config = (await client.CallAsync("config", "getconfig"))["info"]!;
        Assert.Equal(178, config["leds"]!.AsArray().Count);
        Assert.Equal(@"\\.\NUL", config["device"]!["output"]!.GetValue<string>());
        Assert.Equal("tv.local", config["layout"]!["controllers"]![0]!["host"]!.GetValue<string>());
        Assert.Equal("rgb", config["layout"]!["controllers"]![1]!["device"]!["colorOrder"]!.GetValue<string>());

        var info = (await client.CallAsync("serverinfo"))["info"]!;
        var controllers = info["controllers"]!.AsArray();
        Assert.Equal(["tv", "desk"], controllers.Select(c => c!["id"]!.GetValue<string>()));
        Assert.Equal([0, 168], controllers.Select(c => c!["start"]!.GetValue<int>()));
        Assert.Equal([168, 10], controllers.Select(c => c!["ledCount"]!.GetValue<int>()));
        Assert.Equal(["monitor", "ceiling"], controllers[0]!["strips"]!.AsArray().Select(s => s!["id"]!.GetValue<string>()));
        Assert.Equal(91, controllers[0]!["strips"]![1]!["start"]!.GetValue<int>());
        Assert.Equal(178, info["leds"]!.AsArray().Count);
        Assert.Equal(0.5, controllers[1]!["strips"]![0]!["brightness"]!.GetValue<double>());

        // a strip on a controller that doesn't exist is rejected with the path of the problem
        var bad = Layout();
        bad["strips"]![2]!["controllerId"] = "garage";
        var rejected = await client.CallAsync("config", "setconfig", new JsonObject { ["config"] = new JsonObject { ["layout"] = bad } });
        Assert.False(Success(rejected));
        Assert.Equal("layout.strips.2.controllerId", rejected["errors"]![0]!["path"]!.GetValue<string>());
    }

    [Fact]
    public async Task Blink_AddressesAControllerAndAnLed()
    {
        await using var server = await TestServer.StartAsync(NoSmoothing);
        await using var client = await server.ConnectAsync();
        Assert.True(Success(await client.CallAsync("config", "setconfig", new JsonObject { ["config"] = new JsonObject { ["layout"] = Layout() } })));

        var byId = await client.CallAsync("leddevice", "identify", Obj("""{ "ledDeviceType": "blink", "params": { "blinkIndex": 4, "controller": "desk" } }"""));
        Assert.True(Success(byId), Error(byId));
        Assert.Equal(("desk", 4), (byId["info"]!["controller"]!.GetValue<string>(), byId["info"]!["blinkIndex"]!.GetValue<int>()));

        var byPosition = await client.CallAsync("leddevice", "identify", Obj("""{ "ledDeviceType": "blink", "params": { "blinkIndex": 0, "controller": 1 } }"""));
        Assert.Equal("desk", byPosition["info"]!["controller"]!.GetValue<string>());

        // an old client's index counts over all LEDs
        var global = await client.CallAsync("leddevice", "identify", Obj("""{ "ledDeviceType": "blink", "params": { "blinkIndex": 170 } }"""));
        Assert.Equal(("desk", 2), (global["info"]!["controller"]!.GetValue<string>(), global["info"]!["blinkIndex"]!.GetValue<int>()));

        var outside = await client.CallAsync("leddevice", "identify", Obj("""{ "ledDeviceType": "blink", "params": { "blinkIndex": 10, "controller": "desk" } }"""));
        Assert.False(Success(outside));
        Assert.Contains("has no LED 10", Error(outside), StringComparison.Ordinal);

        // held until stopped (one controller, or all)
        var held = await client.CallAsync("leddevice", "identify", Obj("""{ "ledDeviceType": "blink", "params": { "blinkIndex": 5, "controller": "tv", "hold": true } }"""));
        Assert.True(Success(held), Error(held));
        Assert.Equal(("tv", 5, true), (held["info"]!["controller"]!.GetValue<string>(), held["info"]!["blinkIndex"]!.GetValue<int>(), held["info"]!["hold"]!.GetValue<bool>()));
        var stopped = await client.CallAsync("leddevice", "identify", Obj("""{ "ledDeviceType": "blink", "params": { "stop": true, "controller": "tv" } }"""));
        Assert.True(Success(stopped), Error(stopped));
        Assert.True(stopped["info"]!["stopped"]!.GetValue<bool>());
        Assert.True(Success(await client.CallAsync("leddevice", "identify", Obj("""{ "ledDeviceType": "blink", "params": { "stop": true } }"""))));
        Assert.Contains("no controller", Error(await client.CallAsync("leddevice", "identify", Obj("""{ "ledDeviceType": "blink", "params": { "stop": true, "controller": "garage" } }"""))), StringComparison.Ordinal);
        Assert.False(Success(await client.CallAsync("leddevice", "identify", Obj("""{ "ledDeviceType": "blink", "params": { "blinkIndex": 1, "hold": "yes" } }"""))));
    }

    [Fact]
    public async Task LedStreamAndPerformance_CoverEveryController()
    {
        await using var server = await TestServer.StartAsync(NoSmoothing);
        await using var client = await server.ConnectAsync();
        Assert.True(Success(await client.CallAsync("config", "setconfig", new JsonObject { ["config"] = new JsonObject { ["layout"] = Layout() } })));

        var start = await client.CallAsync("ledcolors", "ledstream-start");
        Assert.Equal([168, 10], start["info"]!["controllers"]!.AsArray().Select(c => c!["length"]!.GetValue<int>()));
        var update = client.NextEvent("ledcolors-ledstream-update", e => e["result"]!["leds"]!.AsArray().Count == 178 * 3);
        var ranges = update["result"]!["controllers"]!.AsArray();
        Assert.Equal(["tv", "desk"], ranges.Select(c => c!["id"]!.GetValue<string>()));
        Assert.Equal([0, 168], ranges.Select(c => c!["start"]!.GetValue<int>()));

        await Task.Delay(1500, TestContext.Current.CancellationToken);
        await client.CallAsync("serverinfo", parameters: Obj("""{ "subscribe": ["performance-update"] }"""));
        var reports = client.NextEvent("performance-update")["data"]!["broadcast"]!.AsArray().Where(r => r!["type"]!.GetValue<int>() == 3).ToList();
        Assert.Equal(["tv", "desk"], reports.Select(r => r!["controller"]!.GetValue<string>()));
        Assert.Equal([0, 1], reports.Select(r => r!["id"]!.GetValue<int>()));
        Assert.All(reports, r => Assert.True(r!["param1"]!.GetValue<double>() > 10, r!.ToJsonString()));
        Assert.All(reports, r => Assert.InRange(r!["latencyMs"]!.GetValue<double>(), 0, 100));
        Assert.Equal("Desk", reports[1]!["controllerName"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnOldSingleControllerClient_KeepsWorking()
    {
        await using var server = await TestServer.StartAsync(NoSmoothing);
        await using var client = await server.ConnectAsync();
        var single = Layout();
        single["controllers"]!.AsArray().RemoveAt(1);
        single["strips"]!.AsArray().RemoveAt(2);
        Assert.True(Success(await client.CallAsync("config", "setconfig", new JsonObject { ["config"] = new JsonObject { ["layout"] = single } })));

        // the classic device settings change the layout's controller
        var device = Obj("""{ "config": { "device": { "type": "file", "output": "\\\\.\\NUL", "printTimeStamp": true } } }""");
        Assert.True(Success(await client.CallAsync("config", "setconfig", device)));
        var config = (await client.CallAsync("config", "getconfig"))["info"]!;
        Assert.True(config["layout"]!["controllers"]![0]!["device"]!["printTimeStamp"]!.GetValue<bool>());

        // the classic LED editor replaces the layout
        var leds = Obj("""{ "config": { "leds": [{ "hmin": 0, "hmax": 1, "vmin": 0, "vmax": 1 }, { "hmin": 0, "hmax": 1, "vmin": 0, "vmax": 1 }] } }""");
        Assert.True(Success(await client.CallAsync("config", "setconfig", leds)));
        config = (await client.CallAsync("config", "getconfig"))["info"]!;
        Assert.False(config.AsObject().ContainsKey("layout"));
        Assert.Equal(2, config["leds"]!.AsArray().Count);
        var controller = Assert.Single((await client.CallAsync("serverinfo"))["info"]!["controllers"]!.AsArray());
        Assert.Equal(("controller-1", 2), (controller!["id"]!.GetValue<string>(), controller["ledCount"]!.GetValue<int>()));
        Assert.True(Success(await client.CallAsync("leddevice", "identify", Obj("""{ "ledDeviceType": "blink", "params": { "blinkIndex": 1 } }"""))));
    }
}
