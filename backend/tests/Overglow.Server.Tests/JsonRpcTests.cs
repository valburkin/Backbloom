using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Overglow.Settings;
using Overglow.Settings.Storage;

namespace Overglow.Server.Tests;

public sealed class JsonRpcTests
{
    private const string LocalAdmin = """{ "network": { "localAdminAuth": false } }""";

    private static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();

    private static bool Success(JsonObject reply) => reply["success"]!.GetValue<bool>();

    private static string? Error(JsonObject reply) => reply["error"]?.GetValue<string>();

    [Fact]
    public async Task Authorize_PasswordLoginTokenLoginAndLogout()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = await server.ConnectAsync();

        Assert.False((await client.CallAsync("authorize", "tokenRequired"))["info"]!["required"]!.GetValue<bool>());
        Assert.True((await client.CallAsync("authorize", "adminRequired"))["info"]!["adminRequired"]!.GetValue<bool>());
        Assert.True((await client.CallAsync("authorize", "newPasswordRequired"))["info"]!["newPasswordRequired"]!.GetValue<bool>());

        var denied = await client.CallAsync("config", "getconfig");
        Assert.Equal("No Authorization", Error(denied));
        Assert.Equal("config", denied["command"]!.GetValue<string>());

        Assert.Equal("Password too short", Error(await client.CallAsync("authorize", "login", Obj("""{ "password": "short" }"""))));
        Assert.Equal("No Authorization", Error(await client.CallAsync("authorize", "login", Obj("""{ "password": "wrong-password" }"""))));

        var login = await client.CallAsync("authorize", "login", Obj("""{ "password": "overglow" }"""));
        Assert.Equal("authorize-login", login["command"]!.GetValue<string>());
        var token = login["info"]!["token"]!.GetValue<string>();
        Assert.False((await client.CallAsync("authorize", "adminRequired"))["info"]!["adminRequired"]!.GetValue<bool>());
        Assert.True(Success(await client.CallAsync("config", "getconfig")));

        Assert.True(Success(await client.CallAsync("authorize", "logout")));
        Assert.True((await client.CallAsync("authorize", "adminRequired"))["info"]!["adminRequired"]!.GetValue<bool>());

        Assert.True(Success(await client.CallAsync("authorize", "login", new JsonObject { ["token"] = token })));
        Assert.True(Success(await client.CallAsync("config", "getconfig")));
        Assert.Equal("No Authorization", Error(await client.CallAsync("authorize", "login", new JsonObject { ["token"] = new string('a', 64) })));
    }

    [Fact]
    public async Task Authorize_ChangePasswordAndApiTokens()
    {
        await using var server = await TestServer.StartAsync(LocalAdmin);
        await using var client = await server.ConnectAsync();

        Assert.Equal("Failed to update user password", Error(await client.CallAsync("authorize", "newPassword", Obj("""{ "password": "nope", "newPassword": "long-enough-1" }"""))));
        Assert.True(Success(await client.CallAsync("authorize", "newPassword", Obj("""{ "password": "overglow", "newPassword": "long-enough-1" }"""))));
        Assert.False((await client.CallAsync("authorize", "newPasswordRequired"))["info"]!["newPasswordRequired"]!.GetValue<bool>());

        var created = (await client.CallAsync("authorize", "createToken", Obj("""{ "comment": "Stream Deck" }""")))["info"]!;
        var secret = created["token"]!.GetValue<string>();
        Assert.Equal(36, secret.Length);
        var list = (await client.CallAsync("authorize", "getTokenList"))["info"]!.AsArray();
        Assert.Equal("Stream Deck", Assert.Single(list)!["comment"]!.GetValue<string>());

        using var http = new HttpClient { BaseAddress = server.BaseUri };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/json-rpc") { Content = new StringContent("""{"command":"sysinfo","tan":5}""", Encoding.UTF8) };
        request.Headers.Authorization = new AuthenticationHeaderValue("token", secret);
        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.True(Success(Obj(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))));

        Assert.True(Success(await client.CallAsync("authorize", "deleteToken", new JsonObject { ["id"] = created["id"]!.GetValue<string>() })));
        Assert.Empty((await client.CallAsync("authorize", "getTokenList"))["info"]!.AsArray());
        using var file = new FileStream(Path.Combine(server.DataDirectory, SettingsDatabase.FileName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(file, Encoding.Latin1);
        Assert.DoesNotContain("long-enough-1", await reader.ReadToEndAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authorize_PasswordAndTokensSurviveARestart()
    {
        var dir = TestServer.NewDataDirectory();
        try
        {
            string secret;
            await using (var server = await TestServer.StartInAsync(dir))
            await using (var client = await server.ConnectAsync())
            {
                Assert.True(Success(await client.CallAsync("authorize", "login", Obj("""{ "password": "overglow" }"""))));
                Assert.True(Success(await client.CallAsync("authorize", "newPassword", Obj("""{ "password": "overglow", "newPassword": "long-enough-2" }"""))));
                secret = (await client.CallAsync("authorize", "createToken", Obj("""{ "comment": "Phone" }""")))["info"]!["token"]!.GetValue<string>();
            }

            await using (var server = await TestServer.StartInAsync(dir))
            await using (var client = await server.ConnectAsync())
            {
                Assert.False((await client.CallAsync("authorize", "newPasswordRequired"))["info"]!["newPasswordRequired"]!.GetValue<bool>());
                Assert.Equal("No Authorization", Error(await client.CallAsync("authorize", "login", Obj("""{ "password": "overglow" }"""))));
                Assert.True(Success(await client.CallAsync("authorize", "login", Obj("""{ "password": "long-enough-2" }"""))));
                Assert.Equal("Phone", Assert.Single((await client.CallAsync("authorize", "getTokenList"))["info"]!.AsArray())!["comment"]!.GetValue<string>());
                Assert.True(Success(await client.CallAsync("authorize", "login", new JsonObject { ["token"] = secret })));
            }
        }
        finally
        {
            TestServer.DeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task Backup_SaveAndRestoreWhileRunning()
    {
        await using var server = await TestServer.StartAsync(LocalAdmin);
        await using var client = await server.ConnectAsync();
        await client.CallAsync("serverinfo", parameters: Obj("""{ "subscribe": ["settings-update", "leds-update"] }"""));
        Assert.True(Success(await client.CallAsync("config", "setconfig", Obj("""{ "config": { "logger": { "level": "warn" } } }"""))));
        client.NextEvent("settings-update");

        var backup = (await client.CallAsync("save-db"))["info"]!.AsObject();
        Assert.Equal("Overglow_export_format_v1", backup["version"]!.GetValue<string>());

        var leds = Obj("""{ "config": { "logger": { "level": "verbose" }, "leds": [{ "hmin": 0, "hmax": 1, "vmin": 0, "vmax": 1 }, { "hmin": 0, "hmax": 1, "vmin": 0, "vmax": 1 }] } }""");
        Assert.True(Success(await client.CallAsync("config", "setconfig", leds)));
        client.NextEvent("leds-update");

        var restored = await client.CallAsync("load-db", parameters: new JsonObject { ["config"] = backup.DeepClone() });
        Assert.True(Success(restored), Error(restored));
        Assert.Single(client.NextEvent("leds-update")["data"]!["leds"]!.AsArray());
        var config = (await client.CallAsync("config", "getconfig"))["info"]!;
        Assert.Equal("warn", config["logger"]!["level"]!.GetValue<string>());
        Assert.Single(config["leds"]!.AsArray());
        Assert.True(Success(await client.CallAsync("serverinfo")));
        Assert.Single(Directory.GetFiles(Path.Combine(server.DataDirectory, "backups")));

        var invalid = await client.CallAsync("load-db", parameters: Obj("""{ "config": { "version": "nope", "settings": [] } }"""));
        Assert.StartsWith("Couldn't restore the backup: This isn't a settings backup", Error(invalid), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Backup_NeedsAdminRights()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = await server.ConnectAsync();

        Assert.Equal("No Authorization", Error(await client.CallAsync("save-db")));
        Assert.Equal("No Authorization", Error(await client.CallAsync("load-db", parameters: Obj("""{ "config": {} }"""))));
    }

    [Fact]
    public async Task ServerInfo_ReportsWhatTheUiReads()
    {
        await using var server = await TestServer.StartAsync(LocalAdmin);
        await using var client = await server.ConnectAsync();

        var reply = await client.CallAsync("serverinfo", parameters: Obj("""{ "subscribe": ["performance-update"] }"""));
        var info = reply["info"]!;

        Assert.Equal("serverinfo", reply["command"]!.GetValue<string>());
        Assert.Equal(["ALL", "SMOOTHING", "BLACKBORDER", "SYSTEMGRABBER", "LEDDEVICE"], info["components"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()));
        Assert.True(info["priorities_autoselect"]!.GetValue<bool>());
        Assert.Equal("SYSTEMGRABBER", info["priorities"]![0]!["componentId"]!.GetValue<string>());
        Assert.True(info["priorities"]![0]!["visible"]!.GetValue<bool>());
        Assert.Equal(
            ["ddp", "home_assistant", "lifx", "nanoleaf", "philipshue", "udpartnet", "udpe131", "wiz", "wled", "yeelight", "zigbee2mqtt", "adalight", "skydimo", "sp616e", "file", "synclight"],
            info["ledDevices"]!["available"]!.AsArray().Select(d => d!["name"]!.GetValue<string>()));
        Assert.Equal("advanced", info["imageToLedMappingType"]!.GetValue<string>());
        Assert.Single(info["leds"]!.AsArray());
        Assert.Equal(1.5, info["adjustment"]![0]!["gamma"]!.GetValue<double>());
        Assert.Equal(0, info["currentInstance"]!.GetValue<int>());
        Assert.True(info["instance"]![0]!["running"]!.GetValue<bool>());

        var performance = client.NextEvent("performance-update")["data"]!["broadcast"]!.AsArray();
        Assert.Contains(performance, r => r!["type"]!.GetValue<int>() == 2);
        if (OperatingSystem.IsWindows())
        {
            // the whole computer, as the UI's tiles say
            var ram = JsonNode.Parse(performance.Single(r => r!["type"]!.GetValue<int>() == 5)!["name"]!.GetValue<string>())!;
            Assert.True(ram["takenMem"]!.GetValue<long>() > 100);
            Assert.True(ram["totalPhysMem"]!.GetValue<long>() >= ram["takenMem"]!.GetValue<long>());
            await Task.Delay(100, TestContext.Current.CancellationToken);
            var later = server.Services.GetRequiredService<Diagnostics.PerformanceMonitor>().Report()["broadcast"]!.AsArray();
            var cpu = JsonNode.Parse(later.Single(r => r!["type"]!.GetValue<int>() == 4)!["name"]!.GetValue<string>())!["total"]!.GetValue<double>();
            Assert.InRange(cpu, 0, 100);
        }

        var sys = (await client.CallAsync("sysinfo"))["info"]!;
        Assert.False(string.IsNullOrEmpty(sys["overglow"]!["version"]!.GetValue<string>()));
        Assert.False(string.IsNullOrEmpty(sys["system"]!["hostName"]!.GetValue<string>()));
    }

    [Fact]
    public async Task SetConfig_MergesSectionsRejectsInvalidValuesAndPushesUpdates()
    {
        await using var server = await TestServer.StartAsync(LocalAdmin);
        await using var client = await server.ConnectAsync();
        await client.CallAsync("serverinfo", parameters: Obj("""{ "subscribe": ["settings-update", "leds-update", "imageToLedMapping-update"] }"""));

        var leds = Obj("""{ "config": { "leds": [{ "hmin": 0, "hmax": 0.5, "vmin": 0, "vmax": 1 }, { "hmin": 0.5, "hmax": 1, "vmin": 0, "vmax": 1 }] } }""");
        var saved = await client.CallAsync("config", "setconfig", leds);
        Assert.True(Success(saved), Error(saved));
        Assert.Equal("config-setconfig", saved["command"]!.GetValue<string>());
        Assert.Equal(2, client.NextEvent("leds-update")["data"]!["leds"]!.AsArray().Count);
        Assert.True(client.NextEvent("settings-update")["data"]!.AsObject().ContainsKey("leds"));

        var config = (await client.CallAsync("config", "getconfig"))["info"]!.AsObject();
        Assert.Equal(2, config["leds"]!.AsArray().Count);
        Assert.Equal("file", config["device"]!["type"]!.GetValue<string>());
        Assert.False(config.ContainsKey("layout"));

        var color = config["color"]!.AsObject();
        color["imageToLedMappingType"] = "unicolor_mean";
        Assert.True(Success(await client.CallAsync("config", "setconfig", new JsonObject { ["config"] = new JsonObject { ["color"] = color.DeepClone() } })));
        Assert.Equal("unicolor_mean", client.NextEvent("imageToLedMapping-update")["data"]!["imageToLedMappingType"]!.GetValue<string>());

        color["channelAdjustment"]![0]!["gamma"] = 250;
        var rejected = await client.CallAsync("config", "setconfig", new JsonObject { ["config"] = new JsonObject { ["color"] = color.DeepClone() } });
        Assert.False(Success(rejected));
        Assert.Equal("color.channelAdjustment.0.gamma", rejected["errors"]![0]!["path"]!.GetValue<string>());
        Assert.Contains("must be at most 100", Error(rejected), StringComparison.Ordinal);
        var stored = (await client.CallAsync("config", "getconfig"))["info"]!;
        Assert.Equal(1.5, stored["color"]!["channelAdjustment"]![0]!["gamma"]!.GetValue<double>());

        Assert.Equal("isn't a setting this version supports", (await client.CallAsync("config", "setconfig", Obj("""{ "config": { "flatbufServer": {} } }""")))["errors"]![0]!["message"]!.GetValue<string>());
        var smoothing = await client.CallAsync("config", "setconfig", Obj("""{ "config": { "smoothing": { "time_ms": 99999 } } }"""));
        Assert.Equal("smoothing.time_ms", smoothing["errors"]![0]!["path"]!.GetValue<string>());
        Assert.Equal(150, (await client.CallAsync("config", "getconfig"))["info"]!["smoothing"]!["time_ms"]!.GetValue<int>());

        var schema = (await client.CallAsync("config", "getschema"))["info"]!;
        Assert.NotNull(schema["properties"]!["alldevices"]!["wled"]);
        Assert.NotNull(schema["properties"]!["color"]);
    }

    [Fact]
    public async Task SetConfig_WorksWhileTheLightsAreOff()
    {
        await using var server = await TestServer.StartAsync(LocalAdmin);
        await using var client = await server.ConnectAsync();

        Assert.True(Success(await client.CallAsync("componentstate", parameters: Obj("""{ "componentstate": { "component": "ALL", "state": false } }"""))));

        Assert.True(Success(await client.CallAsync("config", "setconfig", Obj("""{ "config": { "logger": { "level": "warn" } } }"""))));
        Assert.True(Success(await client.CallAsync("componentstate", parameters: Obj("""{ "componentstate": { "component": "LEDDEVICE", "state": false } }"""))));
        var config = (await client.CallAsync("config", "getconfig"))["info"]!.AsObject();
        var smoothing = config["smoothing"]!.AsObject();
        smoothing["time_ms"] = 300;
        var device = config["device"]!.AsObject();
        device["colorOrder"] = "grb";
        var saved = await client.CallAsync("config", "setconfig", new JsonObject { ["config"] = new JsonObject { ["smoothing"] = smoothing.DeepClone(), ["device"] = device.DeepClone() } });
        Assert.True(Success(saved), Error(saved));
        var stored = (await client.CallAsync("config", "getconfig"))["info"]!;
        Assert.Equal((300, "grb"), (stored["smoothing"]!["time_ms"]!.GetValue<int>(), stored["device"]!["colorOrder"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Adjustment_IsAppliedAndStored()
    {
        await using var server = await TestServer.StartAsync(LocalAdmin);
        await using (var client = await server.ConnectAsync())
        {
            await client.CallAsync("serverinfo", parameters: Obj("""{ "subscribe": ["adjustment-update", "settings-update"] }"""));

            var reply = await client.CallAsync("adjustment", parameters: Obj("""{ "adjustment": { "scaleOutput": 0.5 } }"""));
            Assert.True(Success(reply), Error(reply));
            Assert.Equal(0.5, client.NextEvent("adjustment-update")["data"]![0]!["scaleOutput"]!.GetValue<double>());
            Assert.Equal(0.5, client.NextEvent("settings-update")["data"]!["color"]!["channelAdjustment"]![0]!["scaleOutput"]!.GetValue<double>());

            var invalid = await client.CallAsync("adjustment", parameters: Obj("""{ "adjustment": { "gamma": 0 } }"""));
            Assert.Equal("color.channelAdjustment.0.gamma", invalid["errors"]![0]!["path"]!.GetValue<string>());
        }

        var stored = server.Services.GetRequiredService<SettingsServices>().Database.Read(c =>
        {
            using var command = c.CreateCommand();
            command.CommandText = "SELECT value FROM settings WHERE section = 'color'";
            return JsonNode.Parse((string)command.ExecuteScalar()!)!;
        });
        Assert.Equal(0.5, stored["channelAdjustment"]![0]!["scaleOutput"]!.GetValue<double>());
    }

    [Fact]
    public async Task ComponentState_SwitchesAndNotifies()
    {
        await using var server = await TestServer.StartAsync(LocalAdmin);
        await using var client = await server.ConnectAsync();
        await client.CallAsync("serverinfo", parameters: Obj("""{ "subscribe": ["components-update", "priorities-update"] }"""));

        var reply = await client.CallAsync("componentstate", parameters: Obj("""{ "componentstate": { "component": "SYSTEMGRABBER", "state": false } }"""));

        Assert.True(Success(reply));
        var update = client.NextEvent("components-update")["data"]!;
        Assert.Equal(("SYSTEMGRABBER", false), (update["name"]!.GetValue<string>(), update["enabled"]!.GetValue<bool>()));
        // The screen can also time out on its own (no frames in tests), so wait for the update without it.
        var priorities = client.NextEvent(
            "priorities-update",
            e => e["data"]!["priorities"]!.AsArray().All(p => p!["componentId"]?.GetValue<string>() != "SYSTEMGRABBER"));
        Assert.Empty(priorities["data"]!["priorities"]!.AsArray());
        Assert.Contains("isn't available", Error(await client.CallAsync("componentstate", parameters: Obj("""{ "componentstate": { "component": "HDR", "state": true } }"""))), StringComparison.Ordinal);

        foreach (var name in new[] { "SMOOTHING", "BLACKBORDER" })
        {
            var on = name == "BLACKBORDER";
            Assert.True(Success(await client.CallAsync("componentstate", parameters: Obj($$"""{ "componentstate": { "component": "{{name}}", "state": {{(on ? "true" : "false")}} } }"""))));
            var changed = client.NextEvent("components-update")["data"]!;
            Assert.Equal((name, on), (changed["name"]!.GetValue<string>(), changed["enabled"]!.GetValue<bool>()));
        }

        var components = (await client.CallAsync("serverinfo"))["info"]!["components"]!.AsArray();
        Assert.False(components.Single(c => c!["name"]!.GetValue<string>() == "SMOOTHING")!["enabled"]!.GetValue<bool>());
        Assert.True(components.Single(c => c!["name"]!.GetValue<string>() == "BLACKBORDER")!["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Color_ClearAndSourceSelect_DriveThePriorities()
    {
        await using var server = await TestServer.StartAsync(LocalAdmin);
        await using var client = await server.ConnectAsync();
        await client.CallAsync("serverinfo", parameters: Obj("""{ "subscribe": ["priorities-update"] }"""));

        var color = await client.CallAsync("color", parameters: Obj("""{ "priority": 1, "color": [255, 0, 0], "duration": 60000, "origin": "Overglow UI" }"""));
        Assert.True(Success(color), Error(color));
        var priorities = client.NextEvent("priorities-update")["data"]!["priorities"]!.AsArray();
        var visible = priorities[0]!;
        Assert.Equal((1, "COLOR", true), (visible["priority"]!.GetValue<int>(), visible["componentId"]!.GetValue<string>(), visible["visible"]!.GetValue<bool>()));
        Assert.Equal([255, 0, 0], visible["value"]!["RGB"]!.AsArray().Select(v => v!.GetValue<int>()));
        Assert.Equal([0.0, 1.0, 0.5], visible["value"]!["HSL"]!.AsArray().Select(v => v!.GetValue<double>()));
        Assert.StartsWith("Overglow UI@", visible["origin"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.InRange(visible["duration_ms"]!.GetValue<long>(), 50_000, 60_000);
        Assert.Equal("SYSTEMGRABBER", priorities[1]!["componentId"]!.GetValue<string>());
        var preview = new byte[3];
        var engine = server.Services.GetRequiredService<Engine.LightingEngine>();
        await WaitUntilAsync(() => engine.LedColors.CopyLatest(preview) == 3 && preview[0] == 255 && preview[1] < 5);

        // manual selection of the screen, then back to automatic
        Assert.True(Success(await client.CallAsync("sourceselect", parameters: Obj("""{ "priority": 245 }"""))));
        var info = (await client.CallAsync("serverinfo"))["info"]!;
        Assert.False(info["priorities_autoselect"]!.GetValue<bool>());
        Assert.Equal(245, info["priorities"]![0]!["priority"]!.GetValue<int>());
        Assert.False(Success(await client.CallAsync("sourceselect", parameters: Obj("""{ "priority": 7 }"""))));
        Assert.True(Success(await client.CallAsync("sourceselect", parameters: Obj("""{ "auto": true }"""))));
        Assert.Equal(1, (await client.CallAsync("serverinfo"))["info"]!["priorities"]![0]!["priority"]!.GetValue<int>());

        Assert.Equal("Priority 254 is not allowed to be cleared", Error(await client.CallAsync("clear", parameters: Obj("""{ "priority": 254 }"""))));
        Assert.False(Success(await client.CallAsync("color", parameters: Obj("""{ "priority": 0, "color": [1, 2, 3] }"""))));
        Assert.True(Success(await client.CallAsync("clear", parameters: Obj("""{ "priority": 1 }"""))));
        info = (await client.CallAsync("serverinfo"))["info"]!;
        Assert.Equal("SYSTEMGRABBER", Assert.Single(info["priorities"]!.AsArray())!["componentId"]!.GetValue<string>());
        Assert.Empty(info["activeLedColor"]!.AsArray());
    }

    [Fact]
    public async Task Effect_RunsIsListedAndClears()
    {
        await using var server = await TestServer.StartAsync(LocalAdmin);
        await using var client = await server.ConnectAsync();

        var info = (await client.CallAsync("serverinfo"))["info"]!;
        var effects = info["effects"]!.AsArray().Select(e => e!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(10, effects.Count);
        Assert.Contains("Rainbow swirl fast", effects);
        Assert.Equal(4, effects.Count(e => e.StartsWith("Music: ", StringComparison.Ordinal)));
        Assert.Empty(info["activeEffects"]!.AsArray());
        var sound = info["sound"]!;
        Assert.False(sound["active"]!.GetValue<bool>());
        Assert.Equal("default", sound["device"]!.GetValue<string>());
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("default", sound["sound_available"]![0]!.GetValue<string>());
            Assert.Equal("loopback", sound["sound_devices"]![0]!["kind"]!.GetValue<string>());
        }

        var run = await client.CallAsync("effect", parameters: Obj("""{ "priority": 1, "effect": { "name": "Candle" }, "origin": "Overglow UI" }"""));
        Assert.True(Success(run), Error(run));
        info = (await client.CallAsync("serverinfo"))["info"]!;
        var visible = info["priorities"]![0]!;
        Assert.Equal((1, "EFFECT", "Candle", true), (visible["priority"]!.GetValue<int>(), visible["componentId"]!.GetValue<string>(), visible["owner"]!.GetValue<string>(), visible["visible"]!.GetValue<bool>()));
        Assert.StartsWith("Overglow UI@", visible["origin"]!.GetValue<string>(), StringComparison.Ordinal);
        var active = Assert.Single(info["activeEffects"]!.AsArray())!;
        Assert.Equal(("Candle", 1, -1), (active["name"]!.GetValue<string>(), active["priority"]!.GetValue<int>(), active["timeout"]!.GetValue<int>()));

        Assert.Equal("Effect 'Disco' not found", Error(await client.CallAsync("effect", parameters: Obj("""{ "priority": 1, "effect": { "name": "Disco" } }"""))));
        Assert.Equal("priority must be a number from 1 to 253", Error(await client.CallAsync("effect", parameters: Obj("""{ "priority": 254, "effect": { "name": "Candle" } }"""))));
        Assert.False(Success(await client.CallAsync("effect", parameters: Obj("""{ "priority": 2, "effect": { "name": "Candle", "args": { "speed": 2 } } }"""))));

        Assert.True(Success(await client.CallAsync("clear", parameters: Obj("""{ "priority": 1 }"""))));
        info = (await client.CallAsync("serverinfo"))["info"]!;
        Assert.Empty(info["activeEffects"]!.AsArray());
        Assert.Equal("SYSTEMGRABBER", Assert.Single(info["priorities"]!.AsArray())!["componentId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Color_ExpiresAfterItsDuration()
    {
        await using var server = await TestServer.StartAsync(LocalAdmin);
        await using var client = await server.ConnectAsync();

        Assert.True(Success(await client.CallAsync("color", parameters: Obj("""{ "priority": 2, "color": [0, 0, 255], "duration": 300 }"""))));
        var info = (await client.CallAsync("serverinfo"))["info"]!;
        Assert.Equal(2, info["priorities"]![0]!["priority"]!.GetValue<int>());
        Assert.Equal([0, 0, 255], info["activeLedColor"]![0]!["RGB Value"]!.AsArray().Select(v => v!.GetValue<int>()));
        await WaitUntilAsync(async () => (await client.CallAsync("serverinfo"))["info"]!["priorities"]!.AsArray().Count == 1);
        Assert.True(Success(await client.CallAsync("clearall")));
    }

    private static Task WaitUntilAsync(Func<bool> condition) => WaitUntilAsync(() => Task.FromResult(condition()));

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out.");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task LedStream_PushesColorsUntilStopped()
    {
        await using var server = await TestServer.StartAsync(LocalAdmin);
        await using var client = await server.ConnectAsync();

        var start = await client.CallAsync("ledcolors", "ledstream-start");
        Assert.Equal("ledcolors-ledstream-start", start["command"]!.GetValue<string>());

        var update = client.NextEvent("ledcolors-ledstream-update");
        Assert.Equal(start["tan"]!.GetValue<int>(), update["tan"]!.GetValue<int>());
        Assert.Equal(3, update["result"]!["leds"]!.AsArray().Count);

        Assert.True(Success(await client.CallAsync("ledcolors", "ledstream-stop")));
    }

    [Fact]
    public async Task LedDevice_IdentifyDiscoverAndErrors()
    {
        await using var server = await TestServer.StartAsync(LocalAdmin);
        await using var client = await server.ConnectAsync();

        Assert.Equal("leddevice-identify", (await client.CallAsync("leddevice", "identify", Obj("""{ "ledDeviceType": "blink", "params": { "blinkIndex": 0 } }""")))["command"]!.GetValue<string>());
        // E1.31 has no discovery (the tests don't query the network); the drivers' discovery is tested in Devices.Tests
        var discovered = (await client.CallAsync("leddevice", "discover", Obj("""{ "ledDeviceType": "udpe131", "params": {} }""")))["info"]!;
        Assert.Equal("udpe131", discovered["ledDeviceType"]!.GetValue<string>());
        Assert.Empty(discovered["devices"]!.AsArray());
        Assert.Empty((await client.CallAsync("leddevice", "getProperties", Obj("""{ "ledDeviceType": "file", "params": {} }""")))["info"]!["properties"]!.AsObject());
        Assert.True(Success(await client.CallAsync("instance", "switchTo", Obj("""{ "instance": 0 }"""))));
    }

    [Fact]
    public async Task Logging_RepliesFirstThenStreamsHistory()
    {
        await using var server = await TestServer.StartAsync(LocalAdmin);
        await using var client = await server.ConnectAsync();

        var reply = await client.CallAsync("logging", "start");

        Assert.Equal("logging-start", reply["command"]!.GetValue<string>());
        var history = client.NextEvent("logging-update");
        Assert.Null(history["tan"]);
        Assert.Contains(history["result"]!["messages"]!.AsArray(), m => m!["message"]!.GetValue<string>().Contains("Lighting started", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Http_ServesCommandsWithLocalRights()
    {
        await using var server = await TestServer.StartAsync();
        using var http = new HttpClient { BaseAddress = server.BaseUri };

        async Task<JsonObject> Post(string body)
        {
            using var response = await http.PostAsync("/json-rpc", new StringContent(body, Encoding.UTF8), TestContext.Current.CancellationToken);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            return Obj(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        Assert.True(Success(await Post("""{"command":"serverinfo","tan":1}""")));
        Assert.Equal("No Authorization", Error(await Post("""{"command":"config","subcommand":"getconfig","tan":2}""")));
        Assert.Equal("Errors during message parsing, please consult the Overglow log.", Error(await Post("not json")));
        Assert.Equal("Command not implemented", Error(await Post("""{"command":"nope","tan":3}""")));
    }
}
