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
        Assert.Equal(["ALL", "SYSTEMGRABBER", "LEDDEVICE"], info["components"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()));
        Assert.Equal("SYSTEMGRABBER", info["priorities"]![0]!["componentId"]!.GetValue<string>());
        Assert.True(info["priorities"]![0]!["visible"]!.GetValue<bool>());
        Assert.Equal(["ddp", "wled", "file"], info["ledDevices"]!["available"]!.AsArray().Select(d => d!["name"]!.GetValue<string>()));
        Assert.Equal("advanced", info["imageToLedMappingType"]!.GetValue<string>());
        Assert.Single(info["leds"]!.AsArray());
        Assert.Equal(1.5, info["adjustment"]![0]!["gamma"]!.GetValue<double>());
        Assert.Equal(0, info["currentInstance"]!.GetValue<int>());
        Assert.True(info["instance"]![0]!["running"]!.GetValue<bool>());

        var performance = client.NextEvent("performance-update");
        Assert.Contains(performance["data"]!["broadcast"]!.AsArray(), r => r!["type"]!.GetValue<int>() == 2);

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
        Assert.Empty(client.NextEvent("priorities-update")["data"]!["priorities"]!.AsArray());
        Assert.Contains("isn't available", Error(await client.CallAsync("componentstate", parameters: Obj("""{ "componentstate": { "component": "SMOOTHING", "state": true } }"""))), StringComparison.Ordinal);
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
        Assert.Empty((await client.CallAsync("leddevice", "discover", Obj("""{ "ledDeviceType": "wled", "params": {} }""")))["info"]!["devices"]!.AsArray());
        Assert.Equal("Command not implemented", Error(await client.CallAsync("effect", parameters: Obj("""{ "effect": { "name": "Rainbow swirl" } }"""))));
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
