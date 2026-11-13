using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Backbloom.Engine;
using Backbloom.Server.Rpc;
using Backbloom.Settings;
using Backbloom.Settings.Storage;

namespace Backbloom.Server.Tests;

public sealed class JsonRpcTests
{
    private static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();

    private static bool Success(JsonObject reply) => reply["success"]!.GetValue<bool>();

    private static string? Error(JsonObject reply) => reply["error"]?.GetValue<string>();

    private static bool Required(JsonObject reply, string name) => reply["info"]![name]!.GetValue<bool>();

    [Fact]
    public async Task ThisComputer_HasFullAccessWithoutPassword()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = await server.ConnectAsync(origin: $"http://127.0.0.1:{server.BaseUri.Port}");

        Assert.False(Required(await client.CallAsync("authorize", "tokenRequired"), "required"));
        Assert.False(Required(await client.CallAsync("authorize", "adminRequired"), "adminRequired"));
        Assert.True(Required(await client.CallAsync("authorize", "newPasswordRequired"), "newPasswordRequired"));
        Assert.True(Success(await client.CallAsync("config", "getconfig")));

        // there is no factory password
        Assert.Equal("No Authorization", Error(await client.CallAsync("authorize", "login", Obj("""{ "password": "backbloom" }"""))));
        Assert.True(Success(await client.CallAsync("authorize", "logout")));
        Assert.False(Required(await client.CallAsync("authorize", "adminRequired"), "adminRequired"));
    }

    [Fact]
    public async Task OtherDevices_AreRefusedUntilAPasswordIsSet()
    {
        await using var server = await TestServer.StartAsync();
        await Assert.ThrowsAsync<WebSocketException>(server.ConnectFromNetworkAsync);
        using var http = new HttpClient { BaseAddress = server.BaseUri };
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(http, """{"command":"sysinfo","tan":1}""", TestServer.NetworkPeer)).StatusCode);

        await using (var local = await server.ConnectAsync())
        {
            Assert.True(Success(await local.CallAsync("authorize", "newPassword", Obj("""{ "newPassword": "long-enough-1" }"""))));
        }

        await using var remote = await server.ConnectFromNetworkAsync();
        Assert.True(Required(await remote.CallAsync("authorize", "tokenRequired"), "required"));
    }

    [Fact]
    public async Task OtherDevices_NeedThePasswordWhateverTheirAddress()
    {
        await using var server = await TestServer.StartAsync(password: TestServer.Password);
        foreach (var address in new[] { "10.0.0.7", "fe80::1", "2001:db8::5", "::ffff:192.168.1.50" })
        {
            await using var client = await server.ConnectAsync(IPAddress.Parse(address));
            Assert.True(Required(await client.CallAsync("authorize", "adminRequired"), "adminRequired"), address);
            Assert.Equal("No Authorization", Error(await client.CallAsync("serverinfo")));
        }
    }

    [Fact]
    public async Task OtherDevices_SignInWithThePassword()
    {
        await using var server = await TestServer.StartAsync(password: TestServer.Password);
        await using var client = await server.ConnectFromNetworkAsync();

        Assert.True(Required(await client.CallAsync("authorize", "tokenRequired"), "required"));
        Assert.True(Required(await client.CallAsync("authorize", "adminRequired"), "adminRequired"));
        Assert.False(Required(await client.CallAsync("authorize", "newPasswordRequired"), "newPasswordRequired"));

        var denied = await client.CallAsync("config", "getconfig");
        Assert.Equal("No Authorization", Error(denied));
        Assert.Equal("config", denied["command"]!.GetValue<string>());
        Assert.Equal("No Authorization", Error(await client.CallAsync("serverinfo")));

        Assert.Equal("Password too short", Error(await client.CallAsync("authorize", "login", Obj("""{ "password": "short" }"""))));
        Assert.Equal("No Authorization", Error(await client.CallAsync("authorize", "login", Obj("""{ "password": "wrong-password" }"""))));

        var login = await client.CallAsync("authorize", "login", new JsonObject { ["password"] = TestServer.Password });
        Assert.Equal("authorize-login", login["command"]!.GetValue<string>());
        var token = login["info"]!["token"]!.GetValue<string>();
        Assert.False(Required(await client.CallAsync("authorize", "adminRequired"), "adminRequired"));
        Assert.True(Success(await client.CallAsync("config", "getconfig")));

        Assert.True(Success(await client.CallAsync("authorize", "logout")));
        Assert.True(Required(await client.CallAsync("authorize", "adminRequired"), "adminRequired"));
        Assert.Equal("No Authorization", Error(await client.CallAsync("serverinfo")));

        Assert.True(Success(await client.CallAsync("authorize", "login", new JsonObject { ["token"] = token })));
        Assert.True(Success(await client.CallAsync("config", "getconfig")));
        Assert.Equal("No Authorization", Error(await client.CallAsync("authorize", "login", new JsonObject { ["token"] = new string('a', 64) })));
    }

    [Fact]
    public async Task OtherDevices_WaitAfterTooManyWrongPasswords()
    {
        await using var server = await TestServer.StartAsync(password: TestServer.Password);
        await using var client = await server.ConnectFromNetworkAsync();

        var errors = new List<string?>();
        for (var i = 0; i < 10; i++)
        {
            errors.Add(Error(await client.CallAsync("authorize", "login", Obj("""{ "password": "wrong-password" }"""))));
        }

        Assert.Equal("No Authorization", errors[0]);
        Assert.Equal("Too many attempts. Wait a minute and try again.", errors[^1]);
        Assert.Equal("Too many attempts. Wait a minute and try again.", Error(await client.CallAsync("authorize", "login", new JsonObject { ["password"] = TestServer.Password })));
    }

    [Fact]
    public async Task LoopbackPeers_AreThisComputer()
    {
        await using var server = await TestServer.StartAsync();
        foreach (var address in new[] { "127.0.0.1", "127.10.20.30", "::1", "::ffff:127.0.0.1" })
        {
            await using var client = await server.ConnectAsync(IPAddress.Parse(address));
            Assert.False(Required(await client.CallAsync("authorize", "adminRequired"), "adminRequired"), address);
        }
    }

    [Fact]
    public async Task ForwardedHeaders_DontMakeAClientLocal()
    {
        await using var server = await TestServer.StartAsync(password: TestServer.Password);
        using var http = new HttpClient { BaseAddress = server.BaseUri };
        const string GetConfig = """{"command":"config","subcommand":"getconfig","tan":1}""";
        var forwarded = new Dictionary<string, string> { ["X-Forwarded-For"] = "127.0.0.1", ["Forwarded"] = "for=127.0.0.1", ["X-Real-IP"] = "127.0.0.1" };

        using var remote = await PostAsync(http, GetConfig, TestServer.NetworkPeer, headers: forwarded);
        Assert.Equal("No Authorization", Error(Obj(await remote.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))));

        using var local = await PostAsync(http, GetConfig, headers: new Dictionary<string, string> { ["X-Forwarded-For"] = "192.168.1.50" });
        Assert.True(Success(Obj(await local.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))));
    }

    [Fact]
    public async Task LoopbackRequests_NamingAnotherSite_AreRefused()
    {
        // DNS rebinding names the attacker's host; a cross-site request carries the attacker's origin
        (string? Host, string? Origin)[] cases =
        [
            ("evil.example", null), ("evil.example:18290", null), ("127.0.0.1.evil.example", null), ("localhost.evil.example", null),
            (null, "https://evil.example"), (null, "http://evil.example:18290"), (null, "null"), (null, "http://192.168.1.50:18290"), (null, "file://"),
        ];
        await using var server = await TestServer.StartAsync();
        using var http = new HttpClient { BaseAddress = server.BaseUri };
        foreach (var (host, origin) in cases)
        {
            var headers = origin is null ? null : new Dictionary<string, string> { ["Origin"] = origin };
            using var post = await PostAsync(http, """{"command":"config","subcommand":"getconfig","tan":1}""", host: host, headers: headers);
            Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);

            using var page = new HttpRequestMessage(HttpMethod.Get, "/");
            page.Headers.Host = host;
            if (origin is not null)
            {
                page.Headers.Add("Origin", origin);
            }

            using var response = await http.SendAsync(page, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task LoopbackRequests_FromThisComputer_AreServed()
    {
        (string Host, string? Origin)[] cases =
        [
            ("localhost", "http://localhost:4730"), ("LOCALHOST:18290", null), ("127.0.0.1:18290", "http://127.0.0.1:18290"),
            ("127.0.0.2", "https://127.0.0.1"), ("[::1]:18290", "http://[::1]:18290"),
        ];
        await using var server = await TestServer.StartAsync();
        using var http = new HttpClient { BaseAddress = server.BaseUri };
        foreach (var (host, origin) in cases)
        {
            var headers = origin is null ? null : new Dictionary<string, string> { ["Origin"] = origin };
            using var post = await PostAsync(http, """{"command":"config","subcommand":"getconfig","tan":1}""", host: host, headers: headers);
            Assert.True(Success(Obj(await post.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))), host);
        }
    }

    [Fact]
    public async Task WebSocket_FromAnotherSite_IsRefused()
    {
        await using var server = await TestServer.StartAsync();
        await Assert.ThrowsAsync<WebSocketException>(() => server.ConnectAsync(origin: "https://evil.example"));
        await using var client = await server.ConnectAsync(origin: "http://localhost:4730");
        Assert.True(Success(await client.CallAsync("config", "getconfig")));
    }

    [Fact]
    public async Task OtherDevices_MayUseAnyHostAndOrigin()
    {
        await using var server = await TestServer.StartAsync(password: TestServer.Password);
        using var http = new HttpClient { BaseAddress = server.BaseUri };
        using var post = await PostAsync(http, """{"command":"sysinfo","tan":1}""", TestServer.NetworkPeer, host: "my-pc.local:18290",
            headers: new Dictionary<string, string> { ["Origin"] = "http://my-pc.local:18290" });
        Assert.Equal("No Authorization", Error(Obj(await post.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))));
    }

    [Fact]
    public async Task Authorize_PasswordChangesAndApiTokens()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = await server.ConnectAsync();

        Assert.Equal("The password needs at least 8 characters", Error(await client.CallAsync("authorize", "newPassword", Obj("""{ "newPassword": "short" }"""))));
        Assert.True(Success(await client.CallAsync("authorize", "newPassword", Obj("""{ "newPassword": "long-enough-1" }"""))));
        Assert.False(Required(await client.CallAsync("authorize", "newPasswordRequired"), "newPasswordRequired"));

        // this computer may replace it without the current one (a forgotten password)
        Assert.True(Success(await client.CallAsync("authorize", "newPassword", Obj("""{ "newPassword": "long-enough-2" }"""))));

        var created = (await client.CallAsync("authorize", "createToken", Obj("""{ "comment": "Stream Deck" }""")))["info"]!;
        var secret = created["token"]!.GetValue<string>();
        Assert.Equal(36, secret.Length);
        var list = (await client.CallAsync("authorize", "getTokenList"))["info"]!.AsArray();
        Assert.Equal("Stream Deck", Assert.Single(list)!["comment"]!.GetValue<string>());

        using var http = new HttpClient { BaseAddress = server.BaseUri };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/json-rpc") { Content = new StringContent("""{"command":"sysinfo","tan":5}""", Encoding.UTF8) };
        request.Headers.Authorization = new AuthenticationHeaderValue("token", secret);
        request.Headers.Add(TestServer.PeerHeader, TestServer.NetworkPeer.ToString());
        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.True(Success(Obj(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))));

        // another device needs the current password to change it
        await using (var remote = await server.ConnectFromNetworkAsync())
        {
            Assert.True(Success(await remote.CallAsync("authorize", "login", Obj("""{ "password": "long-enough-2" }"""))));
            Assert.Equal("Failed to update user password", Error(await remote.CallAsync("authorize", "newPassword", Obj("""{ "password": "long-enough-1", "newPassword": "long-enough-3" }"""))));
            Assert.True(Success(await remote.CallAsync("authorize", "newPassword", Obj("""{ "password": "long-enough-2", "newPassword": "long-enough-3" }"""))));
        }

        Assert.True(Success(await client.CallAsync("authorize", "deleteToken", new JsonObject { ["id"] = created["id"]!.GetValue<string>() })));
        Assert.Empty((await client.CallAsync("authorize", "getTokenList"))["info"]!.AsArray());
        using var file = new FileStream(Path.Combine(server.DataDirectory, SettingsDatabase.FileName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(file, Encoding.Latin1);
        var content = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("long-enough", content, StringComparison.Ordinal);
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
                Assert.True(Success(await client.CallAsync("authorize", "newPassword", Obj("""{ "newPassword": "long-enough-2" }"""))));
                secret = (await client.CallAsync("authorize", "createToken", Obj("""{ "comment": "Phone" }""")))["info"]!["token"]!.GetValue<string>();
            }

            await using (var server = await TestServer.StartInAsync(dir))
            await using (var client = await server.ConnectFromNetworkAsync())
            {
                Assert.False(Required(await client.CallAsync("authorize", "newPasswordRequired"), "newPasswordRequired"));
                Assert.Equal("No Authorization", Error(await client.CallAsync("authorize", "login", Obj("""{ "password": "backbloom" }"""))));
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
    public async Task RemoteAccess_NeedsAPasswordFirst()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = await server.ConnectAsync();

        var refused = await client.CallAsync("config", "setconfig", Obj("""{ "config": { "network": { "remoteAccess": true } } }"""));
        Assert.Equal(ConfigCommand.PasswordNeeded, Error(refused));
        Assert.False(server.Services.GetRequiredService<ISettingsStore>().GetSection("network")!["remoteAccess"]!.GetValue<bool>());

        // HyperHDR's access options are ignored (older clients send them back)
        Assert.True(Success(await client.CallAsync("config", "setconfig", Obj("""{ "config": { "network": { "remoteAccess": false, "localAdminAuth": true, "apiAuth": false } } }"""))));
        var network = (await client.CallAsync("config", "getconfig"))["info"]!["network"]!.AsObject();
        Assert.Equal(["remoteAccess"], network.Select(p => p.Key));

        Assert.True(Success(await client.CallAsync("authorize", "newPassword", Obj("""{ "newPassword": "long-enough-1" }"""))));
        Assert.True(Success(await client.CallAsync("config", "setconfig", Obj("""{ "config": { "network": { "remoteAccess": true } } }"""))));
    }

    // the screen source shows once the first frame of the test pattern arrived
    private static async Task WaitForTheTestPatternAsync(RpcClient client)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!(await client.CallAsync("serverinfo"))["info"]!["priorities"]![0]!["visible"]!.GetValue<bool>())
        {
            Assert.True(DateTime.UtcNow < deadline, "The test pattern didn't show");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient http, string body, IPAddress? peer = null, string? host = null, Dictionary<string, string>? headers = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/json-rpc") { Content = new StringContent(body, Encoding.UTF8) };
        request.Headers.Host = host;
        if (peer is not null)
        {
            request.Headers.Add(TestServer.PeerHeader, peer.ToString());
        }

        foreach (var (name, value) in headers ?? [])
        {
            request.Headers.Add(name, value);
        }

        return await http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Backup_SaveAndRestoreWhileRunning()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = await server.ConnectAsync();
        await client.CallAsync("serverinfo", parameters: Obj("""{ "subscribe": ["settings-update", "leds-update"] }"""));
        Assert.True(Success(await client.CallAsync("config", "setconfig", Obj("""{ "config": { "logger": { "level": "silent" } } }"""))));
        client.NextEvent("settings-update");

        var backup = (await client.CallAsync("save-db"))["info"]!.AsObject();
        Assert.Equal("Backbloom_export_format_v1", backup["version"]!.GetValue<string>());

        var leds = Obj("""{ "config": { "logger": { "level": "verbose" }, "leds": [{ "hmin": 0, "hmax": 1, "vmin": 0, "vmax": 1 }, { "hmin": 0, "hmax": 1, "vmin": 0, "vmax": 1 }] } }""");
        Assert.True(Success(await client.CallAsync("config", "setconfig", leds)));
        client.NextEvent("leds-update");

        var restored = await client.CallAsync("load-db", parameters: new JsonObject { ["config"] = backup.DeepClone() });
        Assert.True(Success(restored), Error(restored));
        Assert.Single(client.NextEvent("leds-update")["data"]!["leds"]!.AsArray());
        var config = (await client.CallAsync("config", "getconfig"))["info"]!;
        Assert.Equal("silent", config["logger"]!["level"]!.GetValue<string>());
        Assert.Single(config["leds"]!.AsArray());
        Assert.True(Success(await client.CallAsync("serverinfo")));
        Assert.Single(Directory.GetFiles(Path.Combine(server.DataDirectory, "backups")));

        var invalid = await client.CallAsync("load-db", parameters: Obj("""{ "config": { "version": "nope", "settings": [] } }"""));
        Assert.StartsWith("Couldn't restore the backup: This isn't a settings backup", Error(invalid), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Backup_NeedsAdminRights()
    {
        await using var server = await TestServer.StartAsync(password: TestServer.Password);
        await using var client = await server.ConnectFromNetworkAsync();

        Assert.Equal("No Authorization", Error(await client.CallAsync("save-db")));
        Assert.Equal("No Authorization", Error(await client.CallAsync("load-db", parameters: Obj("""{ "config": {} }"""))));
    }

    [Fact]
    public async Task ServerInfo_ReportsWhatTheUiReads()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = await server.ConnectAsync();
        await WaitForTheTestPatternAsync(client);

        var reply = await client.CallAsync("serverinfo", parameters: Obj("""{ "subscribe": ["performance-update"] }"""));
        var info = reply["info"]!;

        Assert.Equal("serverinfo", reply["command"]!.GetValue<string>());
        Assert.Equal(["ALL", "SMOOTHING", "BLACKBORDER", "SYSTEMGRABBER", "LEDDEVICE"], info["components"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()));
        Assert.True(info["priorities_autoselect"]!.GetValue<bool>());
        Assert.Equal("SYSTEMGRABBER", info["priorities"]![0]!["componentId"]!.GetValue<string>());
        Assert.True(info["priorities"]![0]!["visible"]!.GetValue<bool>());
        Assert.Equal(
            ["ddp", "govee", "home_assistant", "lifx", "nanoleaf", "philipshue", "udpartnet", "udpe131", "wiz", "wled", "yeelight", "zigbee2mqtt", "adalight", "skydimo", "sp616e", "file", "synclight"],
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
        Assert.False(string.IsNullOrEmpty(sys["backbloom"]!["version"]!.GetValue<string>()));
        Assert.False(string.IsNullOrEmpty(sys["system"]!["hostName"]!.GetValue<string>()));
    }

    [Fact]
    public async Task SetConfig_MergesSectionsRejectsInvalidValuesAndPushesUpdates()
    {
        await using var server = await TestServer.StartAsync();
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
        Assert.Equal(350, (await client.CallAsync("config", "getconfig"))["info"]!["smoothing"]!["time_ms"]!.GetValue<int>());

        var schema = (await client.CallAsync("config", "getschema"))["info"]!;
        Assert.NotNull(schema["properties"]!["alldevices"]!["wled"]);
        Assert.NotNull(schema["properties"]!["color"]);
    }

    [Fact]
    public async Task SetConfig_WorksWhileTheLightsAreOff()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = await server.ConnectAsync();

        Assert.True(Success(await client.CallAsync("componentstate", parameters: Obj("""{ "componentstate": { "component": "ALL", "state": false } }"""))));

        Assert.True(Success(await client.CallAsync("config", "setconfig", Obj("""{ "config": { "logger": { "level": "silent" } } }"""))));
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
        await using var server = await TestServer.StartAsync();
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
        await using var server = await TestServer.StartAsync();
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
        await using var server = await TestServer.StartAsync();
        await using var client = await server.ConnectAsync();
        await client.CallAsync("serverinfo", parameters: Obj("""{ "subscribe": ["priorities-update"] }"""));

        var color = await client.CallAsync("color", parameters: Obj("""{ "priority": 1, "color": [255, 0, 0], "duration": 60000, "origin": "Backbloom UI" }"""));
        Assert.True(Success(color), Error(color));
        // the first update after subscribing may still be about the screen capture starting up: wait for the color's
        var priorities = client.NextEvent("priorities-update", e => e["data"]!["priorities"]!.AsArray().Any(p => p!["priority"]!.GetValue<int>() == 1))["data"]!["priorities"]!.AsArray();
        var visible = priorities[0]!;
        Assert.Equal((1, "COLOR", true), (visible["priority"]!.GetValue<int>(), visible["componentId"]!.GetValue<string>(), visible["visible"]!.GetValue<bool>()));
        Assert.Equal([255, 0, 0], visible["value"]!["RGB"]!.AsArray().Select(v => v!.GetValue<int>()));
        Assert.Equal([0.0, 1.0, 0.5], visible["value"]!["HSL"]!.AsArray().Select(v => v!.GetValue<double>()));
        Assert.StartsWith("Backbloom UI@", visible["origin"]!.GetValue<string>(), StringComparison.Ordinal);
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
    public async Task EffectPreviews_CoverEveryEffect()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = await server.ConnectAsync();

        var reply = await client.CallAsync("effects", "previews");
        Assert.Equal("effects-previews", reply["command"]!.GetValue<string>());
        var info = reply["info"]!;
        var leds = info["ledCount"]!.GetValue<int>();
        Assert.Equal(server.Services.GetRequiredService<ISettingsStore>().GetSection("leds")!.AsArray().Count, leds);
        Assert.False(info["standard"]!.GetValue<bool>());
        var previews = info["previews"]!.AsArray();
        Assert.Equal(EffectEngine.Available.Select(e => e.Name), previews.Select(p => p!["name"]!.GetValue<string>()));
        foreach (var preview in previews)
        {
            var frames = Convert.FromBase64String(preview!["frames"]!.GetValue<string>());
            Assert.True(frames.Length > 0 && frames.Length % (leds * 3) == 0);
        }

        var one = (await client.CallAsync("effects", "previews", Obj("""{ "name": "Candle" }""")))["info"]!["previews"]!.AsArray();
        Assert.Equal("Candle", Assert.Single(one)!["name"]!.GetValue<string>());
        Assert.Equal("Effect 'Nope' not found", Error(await client.CallAsync("effects", "previews", Obj("""{ "name": "Nope" }"""))));
        Assert.Equal("unknown or missing subcommand", Error(await client.CallAsync("effects", "nope")));
    }

    [Fact]
    public async Task SoundState_IsPushedWhenItChanges()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = await server.ConnectAsync();
        await client.CallAsync("serverinfo", null, Obj("""{ "subscribe": ["sound-update"] }"""));

        Assert.True(Success(await client.CallAsync("config", "setconfig", Obj("""{ "config": { "soundEffect": { "enable": true, "device": "input:none", "enable_smoothing": true } } }"""))));
        var update = client.NextEvent("sound-update", e => e["data"]!["device"]!.GetValue<string>() == "input:none");
        Assert.Equal(OperatingSystem.IsWindows(), update["data"]!["active"]!.GetValue<bool>());
        Assert.False(update["data"]!["running"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Effect_RunsIsListedAndClears()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = await server.ConnectAsync();

        var info = (await client.CallAsync("serverinfo"))["info"]!;
        var effects = info["effects"]!.AsArray().Select(e => e!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(15, effects.Count);
        Assert.All(info["effects"]!.AsArray(), e => Assert.NotEmpty(e!["description"]!.GetValue<string>()));
        Assert.Contains("Rainbow swirl fast", effects);
        Assert.Equal(9, effects.Count(e => e.StartsWith("Music: ", StringComparison.Ordinal)));
        Assert.Empty(info["activeEffects"]!.AsArray());
        var sound = info["sound"]!;

        // capture is on by default (it runs only while a music effect does); without a platform capture it's inactive
        Assert.Equal(OperatingSystem.IsWindows(), sound["active"]!.GetValue<bool>());
        Assert.Equal("default", sound["device"]!.GetValue<string>());
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("default", sound["sound_available"]![0]!.GetValue<string>());
            Assert.Equal("loopback", sound["sound_devices"]![0]!["kind"]!.GetValue<string>());
        }

        var run = await client.CallAsync("effect", parameters: Obj("""{ "priority": 1, "effect": { "name": "Candle" }, "origin": "Backbloom UI" }"""));
        Assert.True(Success(run), Error(run));
        info = (await client.CallAsync("serverinfo"))["info"]!;
        var visible = info["priorities"]![0]!;
        Assert.Equal((1, "EFFECT", "Candle", true), (visible["priority"]!.GetValue<int>(), visible["componentId"]!.GetValue<string>(), visible["owner"]!.GetValue<string>(), visible["visible"]!.GetValue<bool>()));
        Assert.StartsWith("Backbloom UI@", visible["origin"]!.GetValue<string>(), StringComparison.Ordinal);
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
        await using var server = await TestServer.StartAsync();
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
        await using var server = await TestServer.StartAsync();
        await using var client = await server.ConnectAsync();

        var start = await client.CallAsync("ledcolors", "ledstream-start");
        Assert.Equal("ledcolors-ledstream-start", start["command"]!.GetValue<string>());

        var update = client.NextEvent("ledcolors-ledstream-update");
        Assert.Equal(start["tan"]!.GetValue<int>(), update["tan"]!.GetValue<int>());
        Assert.Equal(3, update["result"]!["leds"]!.AsArray().Count);
        Assert.Equal(1.0, update["result"]!["share"]!.GetValue<double>());

        Assert.True(Success(await client.CallAsync("ledcolors", "ledstream-stop")));
    }

    [Fact]
    public async Task LedStream_SendsTheShareTheBrightnessLeft()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = await server.ConnectAsync();
        await client.CallAsync("ledcolors", "ledstream-start");
        Assert.Equal(1.0, client.NextEvent("ledcolors-ledstream-update")["result"]!["share"]!.GetValue<double>());

        Assert.True(Success(await client.CallAsync("adjustment", parameters: Obj("""{ "adjustment": { "scaleOutput": 0.5 } }"""))));
        var share = 1.0;
        for (var i = 0; i < 50 && share >= 1.0; i++)
        {
            share = client.NextEvent("ledcolors-ledstream-update")["result"]!["share"]!.GetValue<double>();
        }

        // half the light: sRGB-encoded about 0.735, with the default contrast (1.5) about 0.63; rounded to 4 places
        Assert.InRange(share, 0.6, 0.75);
        Assert.Equal(Math.Round(share, 4), share);
        Assert.True(Success(await client.CallAsync("ledcolors", "ledstream-stop")));
    }

    [Fact]
    public async Task LedDevice_IdentifyDiscoverAndErrors()
    {
        await using var server = await TestServer.StartAsync();
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
        await using var server = await TestServer.StartAsync("""{ "logger": { "level": "verbose" } }""");
        await using var client = await server.ConnectAsync();

        var reply = await client.CallAsync("logging", "start");

        Assert.Equal("logging-start", reply["command"]!.GetValue<string>());
        var history = client.NextEvent("logging-update");
        Assert.Null(history["tan"]);
        Assert.Contains(history["result"]!["messages"]!.AsArray(), m => m!["message"]!.GetValue<string>().Contains("Lighting started", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Http_ServesCommandsWithTheRightsOfTheClient()
    {
        await using var server = await TestServer.StartAsync(password: TestServer.Password);
        using var http = new HttpClient { BaseAddress = server.BaseUri };

        async Task<JsonObject> Post(string body, IPAddress? peer = null)
        {
            using var response = await PostAsync(http, body, peer);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            return Obj(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        Assert.True(Success(await Post("""{"command":"serverinfo","tan":1}""")));
        Assert.True(Success(await Post("""{"command":"config","subcommand":"getconfig","tan":2}""")));
        Assert.Equal("No Authorization", Error(await Post("""{"command":"serverinfo","tan":1}""", TestServer.NetworkPeer)));
        Assert.Equal("Errors during message parsing, please consult the Backbloom log.", Error(await Post("not json")));
        Assert.Equal("Command not implemented", Error(await Post("""{"command":"nope","tan":3}""")));
    }
}
