using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Backbloom.Devices.Drivers.Wled;

namespace Backbloom.Devices.Tests;

public sealed class WledTests
{
    private static byte[] Leds(int count) => [.. Enumerable.Range(0, count * 3).Select(i => (byte)i)];

    [Fact]
    public void Drgb_SmallStripIsOnePacketWithHeader()
    {
        var rgb = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var packet = new byte[WledProtocol.MaxPacketSize];

        var length = WledProtocol.WriteFrame(rgb, 0, packet);

        Assert.Equal(1, WledProtocol.PacketCount(3));
        Assert.Equal(new byte[] { 2, 255, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, packet[..length]);
    }

    [Fact]
    public void Drgb_490LedsStillFitOnePacket()
    {
        var rgb = Leds(490);
        var packet = new byte[WledProtocol.MaxPacketSize];

        var length = WledProtocol.WriteFrame(rgb, 0, packet);

        Assert.Equal(1, WledProtocol.PacketCount(490));
        Assert.Equal(2 + 1470, length);
        Assert.Equal(new byte[] { 2, 255 }, packet[..2]);
        Assert.Equal(rgb, packet[2..length]);
    }

    [Fact]
    public void Dnrgb_LargeStripIsSplitInto489LedChunksWithBigEndianStartIndex()
    {
        var rgb = Leds(1000);
        var packet = new byte[WledProtocol.MaxPacketSize];

        Assert.Equal(3, WledProtocol.PacketCount(1000));

        var l0 = WledProtocol.WriteFrame(rgb, 0, packet);
        Assert.Equal(4 + (489 * 3), l0);
        Assert.Equal(new byte[] { 4, 255, 0x00, 0x00 }, packet[..4]);
        Assert.Equal(rgb[..(489 * 3)], packet[4..l0]);

        var l1 = WledProtocol.WriteFrame(rgb, 1, packet);
        Assert.Equal(new byte[] { 4, 255, 0x01, 0xE9 }, packet[..4]);
        Assert.Equal(rgb[(489 * 3)..(978 * 3)], packet[4..l1]);

        var l2 = WledProtocol.WriteFrame(rgb, 2, packet);
        Assert.Equal(4 + (22 * 3), l2);
        Assert.Equal(new byte[] { 4, 255, 0x03, 0xD2 }, packet[..4]);
        Assert.Equal(rgb[(978 * 3)..], packet[4..l2]);
    }

    [Theory]
    [InlineData(true, true, 255, null, """{"on":true,"live":true,"bri":255}""")]
    [InlineData(true, true, 128, null, """{"on":true,"live":true,"bri":128}""")]
    [InlineData(true, false, 255, null, """{"on":true,"live":true}""")]
    [InlineData(false, true, 255, null, """{"on":false,"live":false}""")]
    [InlineData(false, true, 255, """{"on":true,"bri":40,"live":false}""", """{"on":true,"bri":40,"live":false}""")]
    public void OnOffRequest_MatchesCppStrings(bool on, bool overrideBrightness, int level, string? saved, string expected) =>
        Assert.Equal(expected, WledProtocol.OnOffRequest(on, overrideBrightness, level, saved));

    [Fact]
    public void Options_ParseHostPortAndDefaults()
    {
        var o = WledOptions.FromConfig(JsonNode.Parse("""{ "type": "wled", "host": "wled-test.local:8080", "maxRetry": 5 }""")!.AsObject());

        Assert.Equal(new WledOptions("wled-test.local", 8080, true, 255, false, 5), o);
    }

    [Fact]
    public async Task Driver_HandshakesStreamsAndSwitchesOff()
    {
        var http = new FakeHttpHandler((request, _) => request.Method == HttpMethod.Get
            ? FakeHttpHandler.Json("""
                { "state": { "on": true, "bri": 40 },
                  "info": { "ver": "0.15.0", "udpport": 21325, "leds": { "count": 3, "maxpwr": 0 }, "wifi": { "signal": 90, "channel": 6 } } }
                """)
            : FakeHttpHandler.Json("""{"success":true}"""));
        var udp = new RecordingChannel();
        using var driver = new WledDriver(new WledOptions("wled-test.local"), Contexts.With(http, udp));

        await driver.OpenAsync(3, TestContext.Current.CancellationToken);
        Assert.True(driver.Write(new byte[] { 10, 20, 30, 40, 50, 60, 70, 80, 90 }));
        await driver.CloseAsync(TestContext.Current.CancellationToken);

        // the name is resolved once per connection, by the shared resolver: HttpClient never resolves it itself
        Assert.Equal(
        [
            (HttpMethod.Get, "http://192.0.2.10/json", null),
            (HttpMethod.Get, "http://192.0.2.10/json/cfg", null),
            (HttpMethod.Put, "http://192.0.2.10/json/state", """{"on":true,"live":true,"bri":255}"""),
            (HttpMethod.Put, "http://192.0.2.10/json/state", """{"on":false,"live":false}"""),
        ], http.Requests);
        Assert.Equal("192.0.2.10:21325", udp.Remote!.ToString());
        Assert.Equal(new byte[] { 2, 255, 10, 20, 30, 40, 50, 60, 70, 80, 90 }, Assert.Single(udp.Packets));
        Assert.False(driver.Write(new byte[3]));
    }

    [Fact]
    public async Task Driver_KeepsTheScopeOfALinkLocalAddress_InEveryRequest()
    {
        var hosts = new List<string>();
        var http = new FakeHttpHandler((request, _) =>
        {
            lock (hosts)
            {
                hosts.Add(request.RequestUri!.IdnHost);
            }

            return request.Method == HttpMethod.Get
                ? FakeHttpHandler.Json("""{ "state": { "on": true }, "info": { "ver": "0.15.0", "leds": { "count": 3 } } }""")
                : FakeHttpHandler.Json("""{"success":true}""");
        });
        var context = new DriverContext(NullLoggerFactory.Instance, new HttpClient(http), () => new RecordingChannel(), (_, _) => ValueTask.FromResult(IPAddress.Parse("fe80::1%12")));
        using var driver = new WledDriver(new WledOptions("wled-test.local"), context);

        await driver.OpenAsync(3, TestContext.Current.CancellationToken);
        await driver.CloseAsync(TestContext.Current.CancellationToken);

        Assert.True(hosts.Count >= 3);
        Assert.All(hosts, h => Assert.Equal("fe80::1%12", h));
    }

    [Fact]
    public async Task Driver_ReadsTheControllersTelemetry_WhileConnected_AndNoticesARestart()
    {
        var polls = new Queue<string>(
        [
            // 10 s later: same boot, brightness changed in the WLED app, a Fahrenheit sensor
            """
            { "state": { "on": true, "bri": 200, "lor": 0 },
              "info": { "ver": "0.15.0", "leds": { "count": 3, "pwr": 1400, "fps": 41, "maxpwr": 0 }, "live": true, "lm": "UDP", "lip": "192.0.2.1",
                        "ws": 1, "arch": "esp32", "wifi": { "signal": 70, "rssi": -68, "channel": 6 }, "freeheap": 160000, "uptime": 3610,
                        "sensor": { "temperature": [104, "°F"] } } }
            """,

            // 10 s later: restarted (up for 5 s), the limiter not in use yet (WLED then reports 0 mA)
            """
            { "state": { "on": true, "bri": 128 }, "info": { "leds": { "pwr": 0, "maxpwr": 0 }, "wifi": { "signal": 70 }, "uptime": 5 } }
            """,
        ]);
        // a controller that just booted may not answer the first request
        var failNextPut = false;
        HttpResponseMessage FailOnce()
        {
            failNextPut = false;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }

        var http = new FakeHttpHandler((request, _) => (request.Method.Method, request.RequestUri!.AbsolutePath) switch
        {
            // on connect: limits per output (the global one is 0), a failed sensor next to the chip's own temperature
            ("GET", "/json") => FakeHttpHandler.Json("""
                { "state": { "on": true, "bri": 128 },
                  "info": { "ver": "0.15.0", "udpport": 21325, "leds": { "count": 3, "pwr": 1500, "fps": 42, "maxpwr": 0 },
                            "wifi": { "signal": 90, "rssi": -55, "channel": 6 }, "freeheap": 150000, "uptime": 3600,
                            "u": { "Temperature": [0, " Sensor Error!"], "Internal temperature": [51.62, "°C"] } } }
                """),
            ("GET", "/json/cfg") => FakeHttpHandler.Json("""
                { "hw": { "led": { "maxpwr": 0, "ins": [ { "type": 22, "start": 0, "len": 2, "ledma": 55, "maxpwr": 1000 }, { "type": 22, "start": 2, "len": 1, "ledma": 255, "maxpwr": 500 } ] } },
                  "if": { "live": { "maxbri": true } } }
                """),
            ("GET", "/json/si") => FakeHttpHandler.Json(polls.Count > 1 ? polls.Dequeue() : polls.Peek()),
            _ when failNextPut => FailOnce(),
            _ => FakeHttpHandler.Json("""{"success":true}"""),
        });
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
        var context = new DriverContext(NullLoggerFactory.Instance, new HttpClient(http), () => new RecordingChannel(), (_, _) => ValueTask.FromResult(IPAddress.Parse("192.0.2.10"))) { Time = time };
        using var driver = new WledDriver(new WledOptions("wled-test.local"), context);
        Assert.Null(driver.Telemetry);

        await driver.OpenAsync(3, TestContext.Current.CancellationToken);
        var first = driver.Telemetry!;

        // 0.15: each output limited on its own (limit - its share of the ESP's 120 mA - 1 mA per LED); the brightness is
        // the one Backbloom just set (brightnessMaxLevel)
        Assert.Equal("938: 0+2 at 55 | 439: 2+1 at 12 brightest", Describe(first.PowerGroups));
        Assert.Equal(
            new ControllerTelemetry(time.GetUtcNow())
            {
                PowerMilliamps = 1500, PowerLimitMilliamps = 1500, MilliampsPerLed = 40.7, Brightness = 255, ForceMaxBrightness = true, On = true,
                FramesPerSecond = 42, LedCount = 3, SignalPercent = 90, Rssi = -55, WifiChannel = 6, FreeMemoryBytes = 150000, UptimeSeconds = 3600,
                TemperatureCelsius = 51.6, TemperatureSource = "Internal temperature", TemperatureIsChip = true, Firmware = "0.15.0",
            },
            first with { PowerGroups = null });

        time.Advance(WledDriver.TelemetryInterval);
        Wait.Until(() => driver.Telemetry != first);
        var second = driver.Telemetry!;
        Assert.Equal((200, 0, 1400, 0), (second.Brightness, second.Restarts, second.PowerMilliamps, second.RealtimeOverride));
        Assert.Equal((true, "UDP", "192.0.2.1", 1, "esp32"), (second.Live, second.LiveMode, second.LiveSource, second.Clients, second.Chip));
        Assert.Equal((40.0, "temperature", false), (second.TemperatureCelsius, second.TemperatureSource, second.TemperatureIsChip));
        Assert.Equal("70%", driver.Status);

        // a restart: counted, and the realtime state is set again, until that succeeds
        var puts = http.Requests.Count(r => r.Method == HttpMethod.Put);
        failNextPut = true;
        time.Advance(WledDriver.TelemetryInterval);
        Wait.Until(() => driver.Telemetry!.Restarts == 1 && http.Requests.Count(r => r.Method == HttpMethod.Put) == puts + 1);
        time.Advance(WledDriver.TelemetryInterval);
        Wait.Until(() => http.Requests.Count(r => r.Method == HttpMethod.Put) == puts + 2);
        Assert.Equal(1, driver.Telemetry!.Restarts);
        Assert.Equal((null, 5L), (driver.Telemetry!.PowerMilliamps, driver.Telemetry.UptimeSeconds));
        Assert.Equal("""{"on":true,"live":true,"bri":255}""", http.Requests.Last(r => r.Method == HttpMethod.Put).Body);

        // closed: no readings, no more requests
        await driver.CloseAsync(TestContext.Current.CancellationToken);
        Assert.Null(driver.Telemetry);
        var requests = http.Requests.Count;
        time.Advance(WledDriver.TelemetryInterval * 3);
        Thread.Sleep(100);
        Assert.Equal(requests, http.Requests.Count);
        // the power settings are read again after the restart, with each try to set the realtime state again
        Assert.Equal(
            ["/json", "/json/cfg", "/json/si", "/json/si", "/json/cfg", "/json/si", "/json/cfg"],
            http.Requests.Where(r => r.Method == HttpMethod.Get).Select(r => new Uri(r.Uri).AbsolutePath));
    }

    // the shapes of 0.14 (global LED current), 0.15 (only the outputs' own limits) and 0.16 / main (global limit first)
    [Theory]
    [InlineData("0.16.0-alpha", """{ "hw": { "led": { "maxpwr": 0, "ins": [ { "len": 10, "ledma": 55, "maxpwr": 0 } ] } } }""", false, null)]
    [InlineData("0.16.0-alpha", """{ "hw": { "led": { "maxpwr": 2000, "ins": [ { "len": 10, "ledma": 0, "maxpwr": 0 } ] } } }""", false, null)]
    [InlineData("0.16.0-alpha", """{ "hw": { "led": { "maxpwr": 2000, "ins": [ { "len": 10, "ledma": 55, "maxpwr": 0 } ] } } }""", true, 2000)]
    [InlineData("0.16.0-alpha", """{ "hw": { "led": { "maxpwr": 0, "ins": [ { "len": 10, "ledma": 55, "maxpwr": 900 }, { "len": 5, "ledma": 0, "maxpwr": 700 } ] } } }""", true, 900)]
    [InlineData("0.15.1", """{ "hw": { "led": { "maxpwr": 2000, "ins": [ { "len": 10, "ledma": 55, "maxpwr": 0 } ] } } }""", false, null)]
    [InlineData("0.15.0-b3", """{ "hw": { "led": { "maxpwr": 2000, "ins": [ { "len": 10, "ledma": 55, "maxpwr": 1200 }, { "len": 10, "ledma": 55, "maxpwr": 800 } ] } } }""", true, 2000)]
    [InlineData("0.15.1", """{ "hw": { "led": { "maxpwr": 0, "ins": [ { "len": 10, "ledma": 55, "maxpwr": 40 }, { "len": 10, "ledma": 55, "maxpwr": 900 } ] } } }""", true, 900)]
    [InlineData(null, """{ "hw": { "led": { "maxpwr": 2000, "ins": [ { "len": 10, "ledma": 55, "maxpwr": 0 } ] } } }""", false, null)]
    [InlineData(null, """{ "hw": { "led": { "maxpwr": 2000, "ins": [ { "len": 10, "ledma": 55, "maxpwr": 1700 } ] } } }""", true, 1700)]
    [InlineData("0.15.1", """{ "hw": "not an object" }""", false, null)]
    [InlineData("0.14.4", """{ "hw": { "led": { "maxpwr": 850, "ledma": 55, "ins": [ { "len": 10 } ] } } }""", true, 850)]
    [InlineData("0.14.4", """{ "hw": { "led": { "maxpwr": 100, "ledma": 55, "ins": [ { "len": 10 } ] } } }""", false, null)]
    public void Config_TheLimiterIsInUse_AsWledDecidesIt(string? version, string cfg, bool inUse, int? limit)
    {
        var config = WledConfig.Read(JsonNode.Parse(cfg)!.AsObject(), version);
        Assert.Equal((inUse, limit), (config.LimiterInUse, config.PowerLimitMilliamps));
    }

    private static string Describe(IReadOnlyList<PowerGroup>? groups) => string.Join(" | ", (groups ?? []).Select(g =>
        $"{g.BudgetMilliamps}: " + string.Join(", ", g.Ranges.Select(r => $"{r.Start}+{r.Length} at {r.MilliampsPerLed}{(r.BrightestChannel ? " brightest" : "")}"))));

    // the groups WLED's limiter holds to a budget, by version; outputs it doesn't limit, or with a white channel, are left out
    [Theory]
    [InlineData("0.15.1", """[ { "type": 22, "start": 0, "len": 50, "ledma": 55, "maxpwr": 400 }, { "type": 22, "start": 50, "len": 300, "ledma": 55, "maxpwr": 0 } ]""", 0, "290: 0+50 at 55")]
    [InlineData("0.15.1", """[ { "type": 22, "start": 0, "len": 10, "ledma": 255, "maxpwr": 900 } ]""", 0, "770: 0+10 at 12 brightest")]
    [InlineData("0.15.1", """[ { "type": 30, "start": 0, "len": 10, "ledma": 55, "maxpwr": 900 }, { "type": 22, "start": 10, "len": 10, "ledma": 55, "maxpwr": 900 } ]""", 0, "830: 10+10 at 55")]
    [InlineData("0.15.1", """[ { "type": 22, "start": 0, "len": 100, "ledma": 55, "maxpwr": 150 } ]""", 0, "")]
    [InlineData("0.16.0", """[ { "type": 22, "start": 0, "len": 100, "ledma": 60, "maxpwr": 0 }, { "type": 51, "start": 100, "len": 68, "ledma": 30, "maxpwr": 0 } ]""", 5313, "5025: 0+100 at 60, 100+68 at 30")]
    [InlineData("0.16.0", """[ { "type": 22, "start": 0, "len": 100, "ledma": 60, "maxpwr": 0 }, { "type": 30, "start": 100, "len": 68, "ledma": 60, "maxpwr": 0 } ]""", 5313, "")]
    [InlineData("0.16.0", """[ { "type": 22, "start": 0, "len": 100, "ledma": 60, "maxpwr": 2000 } ]""", 0, "1780: 0+100 at 60")]
    [InlineData("0.16.0", """[ { "type": 22, "start": 0, "len": 150, "ledma": 55, "maxpwr": 1000 }, { "type": 22, "start": 150, "len": 10, "ledma": 55, "maxpwr": 0 }, { "type": 22, "start": 160, "len": 10, "ledma": 55, "maxpwr": 0 }, { "type": 22, "start": 170, "len": 10, "ledma": 55, "maxpwr": 0 } ]""", 0, "730: 0+150 at 55")]
    [InlineData("0.15.1", """[ { "type": 22, "start": 0, "len": 150, "ledma": 55, "maxpwr": 1000 }, { "type": 22, "start": 150, "len": 10, "ledma": 55, "maxpwr": 0 }, { "type": 22, "start": 160, "len": 10, "ledma": 55, "maxpwr": 0 }, { "type": 22, "start": 170, "len": 10, "ledma": 55, "maxpwr": 0 } ]""", 0, "820: 0+150 at 55")]
    [InlineData(null, """[ { "type": 22, "start": 0, "len": 100, "ledma": 60, "maxpwr": 2000 } ]""", 2000, "")]
    public void Config_ReadsTheGroupsTheLimiterHoldsToABudget(string? version, string outputs, int global, string groups)
    {
        var cfg = JsonNode.Parse($$"""{ "hw": { "led": { "maxpwr": {{global}}, "ins": {{outputs}} } } }""")!.AsObject();
        Assert.Equal(groups, Describe(WledConfig.Read(cfg, version).Groups));
    }

    [Fact]
    public void Config_OldVersions_HoldEveryOutputToTheGlobalLimit()
    {
        var cfg = JsonNode.Parse("""{ "hw": { "led": { "maxpwr": 850, "ledma": 255, "ins": [ { "type": 22, "start": 0, "len": 10 }, { "type": 22, "start": 10, "len": 5 } ] } } }""")!.AsObject();
        Assert.Equal("715: 0+10 at 12 brightest, 10+5 at 12 brightest", Describe(WledConfig.Read(cfg, "0.14.4").Groups));
    }
    [Fact]
    public void Temperature_PrefersASensorOverTheChip_AndSkipsErrors()
    {
        static WledTemperature? Read(string info) => WledTemperature.Read(JsonNode.Parse(info)!.AsObject());

        Assert.Equal(
            new WledTemperature(35.5, "Temperature", false),
            Read("""{ "sensor": { "Internal Temperature": [61, " °C"] }, "u": { "Temperature": [35.5, "°C"] } }"""));
        Assert.Equal(new WledTemperature(61, "Internal Temperature", true), Read("""{ "u": { "Temperature": [0, " Sensor Error!"], "Internal Temperature": [61, " °C"] } }"""));
        Assert.Null(Read("""{ "u": { "Internal Temperature": [-1, " °C"], "Temperature": [-127, "°C"] } }"""));
    }

    [Fact]
    public async Task Driver_ARestartWhileSwitchedOff_IsNoWarningAndNotCounted_AndPowerWithoutTheLimiterIsUnknown()
    {
        var uptime = 3600;
        var http = new FakeHttpHandler((request, _) => request.Method == HttpMethod.Get
            ? FakeHttpHandler.Json($$"""{ "state": { "bri": 128 }, "info": { "leds": { "count": 3, "pwr": 120, "maxpwr": 0 }, "uptime": {{uptime}} } }""")
            : FakeHttpHandler.Json("""{"success":true}"""));
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
        var context = new DriverContext(NullLoggerFactory.Instance, new HttpClient(http), () => new RecordingChannel(), (_, _) => ValueTask.FromResult(IPAddress.Parse("192.0.2.10"))) { Time = time };
        using var driver = new WledDriver(new WledOptions("wled-test.local"), context);

        await driver.OpenAsync(3, TestContext.Current.CancellationToken);
        Assert.Equal((null, null), (driver.Telemetry!.PowerMilliamps, driver.Telemetry.PowerLimitMilliamps));
        await driver.CloseAsync(TestContext.Current.CancellationToken);

        // an hour later the controller has been up for 10 minutes: it restarted while switched off
        time.Advance(TimeSpan.FromHours(1));
        uptime = 600;
        await driver.OpenAsync(3, TestContext.Current.CancellationToken);
        Assert.Equal(0, driver.Telemetry!.Restarts);
        await driver.CloseAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Driver_RestoresTheSavedStateOnPowerOff()
    {
        var http = new FakeHttpHandler((request, _) => request.Method == HttpMethod.Get
            ? FakeHttpHandler.Json("""{ "state": { "on": true, "bri": 40, "live": true }, "info": { "leds": { "count": 1 } } }""")
            : FakeHttpHandler.Json("{}"));
        using var driver = new WledDriver(new WledOptions("10.0.0.5", RestoreOriginalState: true), Contexts.With(http, new RecordingChannel()));

        await driver.OpenAsync(1, TestContext.Current.CancellationToken);
        await driver.CloseAsync(TestContext.Current.CancellationToken);

        Assert.Equal("""{"on":true,"bri":40,"live":false}""", http.Requests[^1].Body);
    }

    [Fact]
    public async Task Driver_OpenFailsWhenTheControllerAnswersWithAnError()
    {
        var http = new FakeHttpHandler((_, _) => new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
        using var driver = new WledDriver(new WledOptions("wled-test.local"), Contexts.With(http, new RecordingChannel()));

        await Assert.ThrowsAsync<HttpRequestException>(async () => await driver.OpenAsync(3, TestContext.Current.CancellationToken));
        Assert.Equal(new RetryPolicy(60, TimeSpan.FromMilliseconds(1500), RetryPolicy.KeepTrying), driver.RetryPolicy);
    }

    [Fact]
    public void Driver_RequiresAHost() =>
        Assert.Throws<ArgumentException>(() => new WledDriver(new WledOptions(""), Contexts.With(new FakeHttpHandler((_, _) => new()), new RecordingChannel())));
}
