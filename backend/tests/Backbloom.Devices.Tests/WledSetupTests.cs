using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Backbloom.Devices.Setup;
using Backbloom.Tests.Fakes;

namespace Backbloom.Devices.Tests;

/// <summary>The WLED step of the guided setup against a fake board (no network).</summary>
public sealed class WledSetupTests
{
    private const string Host = "192.168.1.50";

    private static (WledSetup Setup, FakeWledBoard Board) Create(TimeProvider? time = null)
    {
        var board = new FakeWledBoard();
        var context = Ctx.Create(http: new FakeWledHandler(board), resolveTo: Host);
        return (new WledSetup(time is null ? context : new DriverContext(context.LoggerFactory, context.Http, null, context.ResolveHost) { Time = time, Discovery = context.Discovery }, TimeSpan.Zero, flashStep: TimeSpan.FromMilliseconds(1), restartWait: TimeSpan.FromSeconds(2), applyWait: TimeSpan.Zero), board);
    }

    private static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();

    private static JsonObject Parsed(JsonObject o) => JsonNode.Parse(o.ToJsonString())!.AsObject();

    [Fact]
    public async Task Read_MapsInfoAndCfg()
    {
        var (setup, board) = Create();
        board.Cfg["nw"]!["ins"]![0]!["ssid"] = "Home";
        var read = await setup.ReadAsync(Host, CancellationToken.None);

        Assert.Equal("WLED", read["name"]!.GetValue<string>());
        Assert.Equal("0.15.0", read["version"]!.GetValue<string>());
        Assert.Equal("esp32", read["chip"]!.GetValue<string>());
        Assert.Equal("A0:B1:C2:D3:E4:F5", read["mac"]!.GetValue<string>());
        Assert.Equal(30, read["ledCount"]!.GetValue<int>());
        Assert.Equal(850, read["maxCurrent"]!.GetValue<int>());
        Assert.Equal(55, read["ledCurrent"]!.GetValue<int>());
        Assert.Equal("Home", read["wifi"]!["ssid"]!.GetValue<string>());
        var output = Assert.Single(read["outputs"]!.AsArray())!;
        Assert.Equal(
            """{"index":0,"strip":"ws2812b","type":22,"pin":16,"clockPin":null,"length":30,"start":0,"colorOrder":"GRB","reverse":false}""",
            output.ToJsonString());
        Assert.All(board.Requests, r => Assert.Equal("GET", r.Method));
        Assert.Equal(["/json/info", "/json/cfg"], board.Requests.Select(r => r.Path));
    }

    [Fact]
    public void Read_ReportsTheLimitWledEnforces()
    {
        var cfg = Obj("""
            { "hw": { "led": { "maxpwr": 0, "ins": [
              { "start": 0, "len": 10, "pin": [2], "order": 2, "rev": true, "type": 22, "maxpwr": 1000, "ledma": 255 },
              { "start": 10, "len": 20, "pin": [4, 5], "order": 4, "type": 51, "maxpwr": 1500, "ledma": 30 } ] } } }
            """);
        var read = WledSetup.Describe(Obj("""{ "ver": "0.15.1", "leds": { "count": 30 } }"""), cfg);
        Assert.Equal(2500, read["maxCurrent"]!.GetValue<int>());
        Assert.Equal(12, read["ledCurrent"]!.GetValue<int>());
        var outputs = read["outputs"]!.AsArray();
        Assert.Equal("ws2815", outputs[0]!["strip"]!.GetValue<string>());
        Assert.Equal("BRG", outputs[0]!["colorOrder"]!.GetValue<string>());
        Assert.True(outputs[0]!["reverse"]!.GetValue<bool>());
        Assert.Equal("apa102", outputs[1]!["strip"]!.GetValue<string>());
        Assert.Equal(5, outputs[1]!["clockPin"]!.GetValue<int>());
        Assert.Equal("BGR", outputs[1]!["colorOrder"]!.GetValue<string>());
        Assert.Null(read["wifi"]!["ssid"]);

        // a global limit alone isn't enforced by 0.15: no limit
        var global = Obj("""{ "hw": { "led": { "maxpwr": 2000, "ins": [ { "len": 10, "ledma": 55, "maxpwr": 0 } ] } } }""");
        Assert.Equal(0, WledSetup.Describe(Obj("""{ "ver": "0.15.1" }"""), global)["maxCurrent"]!.GetValue<int>());
        Assert.Equal(2000, WledSetup.Describe(Obj("""{ "ver": "0.16.0-alpha" }"""), global)["maxCurrent"]!.GetValue<int>());
    }

    [Fact]
    public void Write_OutputsReplaceTheListAndKeepWhatBackbloomDoesntSet()
    {
        var board = new FakeWledBoard();
        var (body, restart) = WledSetup.BuildConfig(board.Cfg, Obj("""
            { "outputs": [
                { "strip": "sk6812-rgbw", "length": 60, "colorOrder": "RGB", "reverse": true },
                { "strip": "ws2815", "length": 40, "pin": 17 } ],
              "maxCurrent": 4250, "ledCurrent": 50 }
            """));

        Assert.True(restart);
        var led = body["hw"]!["led"]!;
        Assert.Equal(4250, led["maxpwr"]!.GetValue<int>());
        Assert.Equal(50, led["ledma"]!.GetValue<int>());
        var ins = led["ins"]!.AsArray();
        Assert.Equal(2, ins.Count);

        // output 0 keeps its pin and the fields Backbloom doesn't set (rgbwm, freq, ref)
        Assert.Equal(30, ins[0]!["type"]!.GetValue<int>());
        Assert.Equal("[16]", ins[0]!["pin"]!.ToJsonString());
        Assert.Equal(0, ins[0]!["start"]!.GetValue<int>());
        Assert.Equal(60, ins[0]!["len"]!.GetValue<int>());
        Assert.Equal(1, ins[0]!["order"]!.GetValue<int>());
        Assert.True(ins[0]!["rev"]!.GetValue<bool>());
        Assert.Equal(50, ins[0]!["ledma"]!.GetValue<int>());
        Assert.NotNull(ins[0]!["rgbwm"]);

        // the limit split by LED count (60 : 40), the global value for the versions that read it
        Assert.Equal(2550, ins[0]!["maxpwr"]!.GetValue<int>());
        Assert.Equal(1700, ins[1]!["maxpwr"]!.GetValue<int>());

        // output 1 follows output 0, WS2815 uses WLED's own current model
        Assert.Equal(22, ins[1]!["type"]!.GetValue<int>());
        Assert.Equal("[17]", ins[1]!["pin"]!.ToJsonString());
        Assert.Equal(60, ins[1]!["start"]!.GetValue<int>());
        Assert.Equal(0, ins[1]!["order"]!.GetValue<int>());
        Assert.Equal(255, ins[1]!["ledma"]!.GetValue<int>());
        Assert.False(ins[1]!["rev"]!.GetValue<bool>());
        Assert.Null(body["nw"]);
        Assert.Null(body["id"]);
    }

    /// <summary>The written limit is enforced by WLED 0.14 (global), 0.15 (per output) and 0.16 / main (global first).</summary>
    [Theory]
    [InlineData("0.14.4")]
    [InlineData("0.15.1")]
    [InlineData("0.16.0-alpha")]
    [InlineData(null)]
    public void Write_TheLimitIsEnforcedByEveryVersion(string? version)
    {
        var cfg = new FakeWledBoard().Cfg;
        var body = WledSetup.BuildConfig(cfg, Obj("""{ "maxCurrent": 1700, "ledCurrent": 50 }""")).Body;
        var written = Parsed(body);
        if (version == "0.14.4")
        {
            // 0.14 has no LED current per output
            foreach (var output in written["hw"]!["led"]!["ins"]!.AsArray())
            {
                output!.AsObject().Remove("ledma");
                output.AsObject().Remove("maxpwr");
            }
        }

        var config = Drivers.Wled.WledConfig.Read(written, version);
        Assert.True(config.LimiterInUse);
        Assert.Equal(1700, config.PowerLimitMilliamps);
    }

    [Fact]
    public void Write_KeepsTheBoardsLimitSplitOverNewOutputs()
    {
        // no maxCurrent: the board's 850 mA is split again over both outputs (not copied from output 0)
        var body = WledSetup.BuildConfig(new FakeWledBoard().Cfg, Obj("""{ "outputs": [{ "strip": "ws2812b", "length": 30 }, { "strip": "ws2812b", "length": 10, "pin": 17 }] }""")).Body;
        var ins = body["hw"]!["led"]!["ins"]!.AsArray();
        Assert.Equal([638, 212], ins.Select(o => o!["maxpwr"]!.GetValue<int>()));
        Assert.Equal(850, body["hw"]!["led"]!["maxpwr"]!.GetValue<int>());

        Assert.Equal([0, 0], WledSetup.SplitLimit(0, [10, 20]));
        Assert.Equal([334, 333, 333], WledSetup.SplitLimit(1000, [1, 1, 1]));

        // every output gets at least its share of the 100 mA WLED 0.15 keeps for the ESP (else it isn't limited at all),
        // taken from the largest part so the total stays the limit
        Assert.Equal([50, 950], WledSetup.SplitLimit(1000, [0, 20]));
        Assert.Equal([200, 50], WledSetup.SplitLimit(250, [490, 10]));
        Assert.Equal([182, 34, 34], WledSetup.SplitLimit(250, [490, 10, 10]));
        var cfg = Obj("""{ "hw": { "led": { "ins": [ { "len": 490, "ledma": 55, "maxpwr": 200 }, { "len": 10, "ledma": 55, "maxpwr": 50 } ] } } }""");
        Assert.True(Drivers.Wled.WledConfig.OutputLimited(cfg, "0.15.1", 1));
    }

    [Fact]
    public void Write_RestartsOnlyWhenWledNeedsIt()
    {
        var cfg = new FakeWledBoard().Cfg;

        // same type and pin: length, order, reverse and currents apply at once
        Assert.False(WledSetup.BuildConfig(cfg, Obj("""{ "outputs": [{ "strip": "ws2812b", "length": 45, "colorOrder": "RGB" }] }""")).Restart);
        Assert.False(WledSetup.BuildConfig(cfg, Obj("""{ "maxCurrent": 0, "name": "Desk" }""")).Restart);
        Assert.True(WledSetup.BuildConfig(cfg, Obj("""{ "outputs": [{ "strip": "ws2812b", "length": 45, "pin": 2 }] }""")).Restart);
        Assert.True(WledSetup.BuildConfig(cfg, Obj("""{ "outputs": [{ "strip": "sk6812-rgbw", "length": 30 }] }""")).Restart);

        var (body, restart) = WledSetup.BuildConfig(cfg, Obj("""{ "wifi": { "ssid": " Home ", "password": "secret" } }"""));
        Assert.True(restart);
        Assert.Equal("""{"nw":{"ins":[{"ssid":" Home ","ip":[0,0,0,0],"psk":"secret"}]}}""", body.ToJsonString());

        // currents alone keep the outputs as they are
        var currents = WledSetup.BuildConfig(cfg, Obj("""{ "ledCurrent": 20 }""")).Body;
        Assert.Equal(20, currents["hw"]!["led"]!["ins"]![0]!["ledma"]!.GetValue<int>());
        Assert.Equal(30, currents["hw"]!["led"]!["ins"]![0]!["len"]!.GetValue<int>());
        Assert.Equal(850, currents["hw"]!["led"]!["maxpwr"]!.GetValue<int>());

        // no limit: none anywhere
        var off = WledSetup.BuildConfig(cfg, Obj("""{ "maxCurrent": 0 }""")).Body;
        Assert.Equal(0, off["hw"]!["led"]!["maxpwr"]!.GetValue<int>());
        Assert.Equal(0, off["hw"]!["led"]!["ins"]![0]!["maxpwr"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("""{ "outputs": [{ "strip": "neon", "length": 5 }] }""", "outputs[0]: unknown strip type 'neon'")]
    [InlineData("""{ "outputs": [{ "length": 5 }] }""", "outputs[0].strip is required")]
    [InlineData("""{ "outputs": [{ "strip": "ws2812b", "length": 0 }] }""", "outputs[0].length must be 1 to 16384 LEDs")]
    [InlineData("""{ "outputs": [{ "strip": "ws2812b", "length": 5 }, { "strip": "ws2812b", "length": 5 }] }""", "outputs[1].pin is required (the board has no output 1 yet)")]
    [InlineData("""{ "outputs": [{ "strip": "apa102", "length": 5 }] }""", "outputs[0].clockPin is required for apa102 (data and clock)")]
    [InlineData("""{ "outputs": [{ "strip": "ws2812b", "length": 5, "colorOrder": "WRGB" }] }""", "outputs[0].colorOrder must be one of GRB, RGB, BRG, RBG, BGR, GBR")]
    [InlineData("""{ "outputs": [] }""", "outputs needs at least one output")]
    [InlineData("""{ "maxCurrent": 100 }""", "maxCurrent must be 0 (no limit) or 250 to 65000 mA")]
    [InlineData("""{ "ledCurrent": 255 }""", "ledCurrent must be 1 to 254 mA")]
    [InlineData("""{ "maxCurrent": "lots" }""", "maxCurrent must be a whole number")]
    [InlineData("""{ "wifi": { "ssid": "", "password": "x" } }""", "wifi.ssid must have 1 to 32 characters")]
    [InlineData("""{ "wifi": { "ssid": "Home" } }""", "wifi.password must be a text of at most 64 characters")]
    [InlineData("""{ "name": "" }""", "The board’s name must have 1 to 32 characters")]
    public void Write_RejectsInvalidRequests(string request, string error) =>
        Assert.Equal(error, Assert.Throws<HardwareSetupException>(() => WledSetup.BuildConfig(new FakeWledBoard().Cfg, Obj(request))).Message);

    [Fact]
    public async Task Write_PostsTheConfigThenRestarts()
    {
        var (setup, board) = Create();
        Assert.True(await setup.WriteAsync(Host, Obj("""{ "wifi": { "ssid": "Home", "password": "secret" } }"""), CancellationToken.None));
        Assert.Equal(["GET /json/info", "GET /json/cfg", "POST /json/cfg", "POST /json/state"], board.Requests.Select(r => $"{r.Method} {r.Path}"));
        Assert.Equal("""{"rb":true}""", board.Requests[3].Body);
        Assert.Equal(1, board.Restarts);

        Assert.False(await setup.WriteAsync(Host, Obj("""{ "name": "Desk" }"""), CancellationToken.None));
        Assert.Equal("Desk", board.Cfg["id"]!["name"]!.GetValue<string>());
        Assert.Equal(1, board.Restarts);
    }

    [Fact]
    public async Task Write_WithoutARestart_ReturnsOnceTheBoardHasTheNewOutputs()
    {
        // WLED rebuilds its outputs a moment after the write (with every LED black): a check lit before that is wiped
        var (setup, board) = Create();
        board.ApplyAfterReads = 2;
        Assert.False(await setup.WriteAsync(Host, Obj("""{ "outputs": [ { "strip": "ws2812b", "length": 77, "pin": 16 } ] }"""), CancellationToken.None));

        Assert.Equal(77, board.Info["leds"]!["count"]!.GetValue<int>());
        var afterPost = board.Requests.SkipWhile(r => !(r.Method == "POST" && r.Path == "/json/cfg")).Skip(1).ToList();
        Assert.Equal(3, afterPost.Count(r => r.Path == "/json/info"));
        Assert.Equal(0, board.Restarts);
    }

    [Fact]
    public async Task Write_AShorterOutputTurnsEveryLedBlackFirst()
    {
        // LEDs past an output's new end would keep their last colors (a bright "last LED" from the count check)
        var (setup, board) = Create();
        var states = board.PostedStates.Count;
        Assert.False(await setup.WriteAsync(Host, Obj("""{ "outputs": [ { "strip": "ws2812b", "length": 40, "pin": 16 } ] }"""), CancellationToken.None));
        Assert.Equal(states, board.PostedStates.Count);

        Assert.False(await setup.WriteAsync(Host, Obj("""{ "outputs": [ { "strip": "ws2812b", "length": 25, "pin": 16 } ] }"""), CancellationToken.None));
        var black = board.Requests.FindIndex(r => r.Method == "POST" && r.Path == "/json/state");
        Assert.True(black >= 0 && black < board.Requests.FindLastIndex(r => r.Method == "POST" && r.Path == "/json/cfg"));
        Assert.Equal("""[0,40,"000000"]""", board.PostedStates[^1]["seg"]![0]!["i"]!.ToJsonString());

        // the board's own state comes back with off
        await setup.TestAsync(Host, 0, WledTestPattern.Off, null, null, CancellationToken.None);
        Assert.Equal(9, board.PostedStates[^1]["seg"]![0]!["fx"]!.GetValue<int>());
    }

    [Fact]
    public async Task Probe_TheBoardIsDarkAfterTheSetup_AndKeepFalsePutsItsBootSettingBack()
    {
        // the search, the found strips written (fewer outputs: every LED black first), the checks, then off
        var (setup, board) = Create();
        board.Cfg["def"] = Obj("""{ "ps": 0, "on": true, "bri": 128 }""");
        await setup.ProbeStartAsync(Host, 1, CancellationToken.None);
        await setup.TestAsync(Host, 1, WledTestPattern.Color, null, (255, 255, 255), CancellationToken.None);
        var before = board.PostedStates.Count;
        Assert.True(await setup.WriteAsync(Host, Obj("""{ "outputs": [ { "strip": "ws2812b", "length": 60, "pin": 2 } ] }"""), CancellationToken.None));
        Assert.Equal("""[0,2000,"000000"]""", board.PostedStates[before]["seg"]![0]!["i"]!.ToJsonString());
        Assert.False(await setup.ProbeEndAsync(Host, keep: true, CancellationToken.None));
        await setup.TestAsync(Host, 0, WledTestPattern.Range, 10, null, CancellationToken.None);

        // off: the state from before the search, dark (not the search's lit white)
        await setup.TestAsync(Host, 0, WledTestPattern.Off, null, null, CancellationToken.None);
        var restored = board.PostedStates[^1];
        Assert.False(restored["on"]!.GetValue<bool>());
        Assert.Equal(9, restored["seg"]![0]!["fx"]!.GetValue<int>());

        // a search given up: the LED settings and "on at boot" as they were
        var (other, fresh) = Create();
        fresh.Cfg["def"] = Obj("""{ "ps": 0, "on": true, "bri": 128 }""");
        await other.ProbeStartAsync(Host, 1, CancellationToken.None);
        Assert.False(fresh.Cfg["def"]!["on"]!.GetValue<bool>());
        Assert.True(await other.ProbeEndAsync(Host, keep: false, CancellationToken.None));
        Assert.Equal("""{"ps":0,"on":true,"bri":128}""", fresh.Cfg["def"]!.ToJsonString());
    }

    [Fact]
    public async Task Write_RefusesALockedOrLiveBoard()
    {
        var (setup, board) = Create();
        board.Locked = true;
        var error = await Assert.ThrowsAsync<HardwareSetupException>(() => setup.WriteAsync(Host, Obj("""{ "name": "Desk" }"""), CancellationToken.None));
        Assert.Equal("The board’s settings are locked with a PIN: unlock them in WLED first", error.Message);

        board.Locked = false;
        board.Info["live"] = true;
        var posts = board.Requests.Count(r => r.Method == "POST");
        var live = await Assert.ThrowsAsync<HardwareSetupException>(() => setup.WriteAsync(Host, Obj("""{ "name": "Desk" }"""), CancellationToken.None));
        Assert.Equal("Another program is sending colors to this board right now: stop it first", live.Message);
        Assert.Equal(posts, board.Requests.Count(r => r.Method == "POST"));
        Assert.Equal("WLED", board.Cfg["id"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Tests_SaveTheStateFirstAndRestoreItOnOff()
    {
        var (setup, board) = Create();
        board.State["seg"]!.AsArray().Add((JsonNode)Obj("""{ "id": 1, "start": 20, "stop": 30, "on": true, "fx": 2 }"""));
        await setup.TestAsync(Host, 0, WledTestPattern.First, null, null, CancellationToken.None);
        await setup.TestAsync(Host, 0, WledTestPattern.Range, 12, null, CancellationToken.None);
        await setup.TestAsync(Host, 0, WledTestPattern.Color, null, (0, 255, 0), CancellationToken.None);
        Assert.Equal([Host], setup.HostsWithSavedState);

        var states = board.PostedStates;
        Assert.Equal("""[0,30,"000000",0,"FFFFFF"]""", states[0]["seg"]![0]!["i"]!.ToJsonString());
        Assert.Equal(WledSetup.LowBrightness, states[0]["bri"]!.GetValue<int>());

        // the other segment is off while the checks show
        Assert.Equal("""{"id":1,"on":false}""", states[0]["seg"]![1]!.ToJsonString());

        // dim enough to tell from the last LED, bright enough to see after WLED's gamma correction
        Assert.Equal("""[0,30,"000000",0,11,"A0A0A0",11,"FFFFFF"]""", states[1]["seg"]![0]!["i"]!.ToJsonString());
        Assert.Equal("""[0,30,"000000",0,30,"00FF00"]""", states[2]["seg"]![0]!["i"]!.ToJsonString());

        await setup.TestAsync(Host, 0, WledTestPattern.Off, null, null, CancellationToken.None);
        var restored = board.PostedStates[^1];
        Assert.Null(restored["ps"]);
        Assert.Equal(9, restored["seg"]![0]!["fx"]!.GetValue<int>());
        Assert.False(restored["seg"]![0]!["frz"]!.GetValue<bool>());
        Assert.True(restored["seg"]![1]!["on"]!.GetValue<bool>());
        Assert.Equal(128, restored["bri"]!.GetValue<int>());
        Assert.Empty(setup.HostsWithSavedState);

        // nothing saved: off sends nothing
        var before = board.Requests.Count;
        await setup.TestAsync(Host, 0, WledTestPattern.Off, null, null, CancellationToken.None);
        Assert.Equal(before, board.Requests.Count);
    }

    [Fact]
    public async Task Tests_RestoreWithoutTheOldBoundsAfterTheLedCountChanged()
    {
        var (setup, board) = Create();
        await setup.TestAsync(Host, 0, WledTestPattern.First, null, null, CancellationToken.None);
        Assert.False(await setup.WriteAsync(Host, Obj("""{ "outputs": [{ "strip": "ws2812b", "length": 45 }] }"""), CancellationToken.None));
        board.Info["leds"]!["count"] = 45;
        await setup.TestAsync(Host, 0, WledTestPattern.Color, null, null, CancellationToken.None);
        Assert.Equal("""[0,45,"000000",0,45,"FF0000"]""", board.PostedStates[^1]["seg"]![0]!["i"]!.ToJsonString());

        await setup.TestAsync(Host, 0, WledTestPattern.Off, null, null, CancellationToken.None);
        var segment = board.PostedStates[^1]["seg"]![0]!.AsObject();
        Assert.False(segment.ContainsKey("start") || segment.ContainsKey("stop") || segment.ContainsKey("len"));
        Assert.Equal(9, segment["fx"]!.GetValue<int>());
    }

    [Fact]
    public async Task Tests_ForgetASavedStateAfterHalfAnHour()
    {
        var time = new FakeTimeProvider();
        var (setup, board) = Create(time);
        await setup.TestAsync(Host, 0, WledTestPattern.First, null, null, CancellationToken.None);
        time.Advance(WledSetup.SavedStateLifetime + TimeSpan.FromSeconds(1));

        var before = board.Requests.Count;
        await setup.TestAsync(Host, 0, WledTestPattern.Off, null, null, CancellationToken.None);
        Assert.Equal(before, board.Requests.Count);
        Assert.Empty(setup.HostsWithSavedState);
    }

    [Fact]
    public async Task Tests_EachCheckKeepsTheSavedStateLonger()
    {
        var time = new FakeTimeProvider();
        var (setup, board) = Create(time);
        await setup.TestAsync(Host, 0, WledTestPattern.First, null, null, CancellationToken.None);

        // wiring between checks: 35 minutes after the first, 20 after the last
        time.Advance(TimeSpan.FromMinutes(15));
        await setup.TestAsync(Host, 0, WledTestPattern.Color, null, null, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(20));
        await setup.TestAsync(Host, 0, WledTestPattern.Off, null, null, CancellationToken.None);
        Assert.Equal(9, board.PostedStates[^1]["seg"]![0]!["fx"]!.GetValue<int>());
        Assert.Empty(setup.HostsWithSavedState);
    }

    [Fact]
    public void White_OnAnOutputWledDoesntLimit_StaysLow()
    {
        // 0.15: output 1's limit is below its share of the ESP's 100 mA, so WLED doesn't limit it (output 0 is)
        var board = new FakeWledBoard();
        board.Info["leds"]!["count"] = 40;
        var cfg = Obj("""
            { "hw": { "led": { "maxpwr": 0, "ins": [
              { "start": 0, "len": 30, "pin": [16], "type": 22, "ledma": 55, "maxpwr": 1000 },
              { "start": 30, "len": 10, "pin": [17], "type": 22, "ledma": 55, "maxpwr": 20 } ] } } }
            """);
        Assert.Equal(255, WledSetup.TestState(board.Info, cfg, board.State, 0, WledTestPattern.White, null, null)["bri"]!.GetValue<int>());
        Assert.Equal(WledSetup.LowBrightness, WledSetup.TestState(board.Info, cfg, board.State, 1, WledTestPattern.White, null, null)["bri"]!.GetValue<int>());

        // 0.16 limits both with the global limit
        board.Info["ver"] = "0.16.0-alpha";
        cfg["hw"]!["led"]!["maxpwr"] = 1500;
        Assert.Equal(255, WledSetup.TestState(board.Info, Parsed(cfg), board.State, 1, WledTestPattern.White, null, null)["bri"]!.GetValue<int>());
    }

    [Fact]
    public void White_IsFullBrightnessOnlyWhenTheBoardsVersionEnforcesTheLimit()
    {
        var board = new FakeWledBoard();
        JsonObject Cfg() => Parsed(board.Cfg);
        int White() => WledSetup.TestState(board.Info, Cfg(), board.State, 0, WledTestPattern.White, null, null)["bri"]!.GetValue<int>();

        // 0.15 with its output's own limit
        Assert.Equal(255, White());

        // no LED current: not limited
        board.Cfg["hw"]!["led"]!["ins"]![0]!["ledma"] = 0;
        Assert.Equal(WledSetup.LowBrightness, White());

        // only the global limit: 0.15 ignores it (the case that lit a strip at full brightness unlimited)
        board.Cfg["hw"]!["led"]!["ins"]![0]!["ledma"] = 55;
        board.Cfg["hw"]!["led"]!["ins"]![0]!["maxpwr"] = 0;
        board.Cfg["hw"]!["led"]!["maxpwr"] = 2000;
        Assert.Equal(WledSetup.LowBrightness, White());

        // ... which 0.16 enforces
        board.Info["ver"] = "0.16.0-alpha";
        Assert.Equal(255, White());

        // unknown version: only when every version would limit it
        board.Info.Remove("ver");
        Assert.Equal(WledSetup.LowBrightness, White());
    }

    [Fact]
    public async Task Tests_RefuseWhatTheBoardDoesntHave()
    {
        var (setup, board) = Create();
        Assert.Equal("The board has no output 1 (it has 1)", (await Assert.ThrowsAsync<HardwareSetupException>(() => setup.TestAsync(Host, 1, WledTestPattern.First, null, null, CancellationToken.None))).Message);
        Assert.Equal("count must be 1 to 30 (the output's LEDs)", (await Assert.ThrowsAsync<HardwareSetupException>(() => setup.TestAsync(Host, 0, WledTestPattern.Range, 31, null, CancellationToken.None))).Message);

        board.Info["live"] = true;
        Assert.StartsWith("Another program is sending colors", (await Assert.ThrowsAsync<HardwareSetupException>(() => setup.TestAsync(Host, 0, WledTestPattern.First, null, null, CancellationToken.None))).Message, StringComparison.Ordinal);
        Assert.Empty(board.PostedStates);
    }

    [Fact]
    public async Task ABoardThatIsntWled()
    {
        var context = Ctx.Create(http: new FakeHttpHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound)), resolveTo: "192.168.1.9");
        using var setup = new WledSetup(context);
        var error = await Assert.ThrowsAsync<HardwareSetupException>(() => setup.ReadAsync("192.168.1.9", CancellationToken.None));
        Assert.Equal("The board at 192.168.1.9 isn’t a WLED board, or too old (/json/info: HTTP 404)", error.Message);
    }

    [Fact]
    public async Task ABoardThatDoesntAnswer_SaysOnlyThat()
    {
        var context = Ctx.Create(http: new FakeHttpHandler((_, _) => throw new HttpRequestException("No connection could be made because the target machine actively refused it (192.168.1.9:80)")), resolveTo: "192.168.1.9");
        using var setup = new WledSetup(context);
        var error = await Assert.ThrowsAsync<HardwareSetupException>(() => setup.ReadAsync("wled-desk.local", CancellationToken.None));
        Assert.Equal("Couldn’t reach wled-desk.local", error.Message);
    }

    // any name: what it resolves to decides
    [Theory]
    [InlineData("192.168.1.50", null, true)]
    [InlineData("10.0.0.7:8080", null, true)]
    [InlineData("fe80::1%12", null, true)]
    [InlineData("[fe80::1%12]:80", null, true)]
    [InlineData("wled-desk", "192.168.1.50", true)]
    [InlineData("wled-desk.local", "192.168.1.50", true)]
    [InlineData("wled.lan", "192.168.1.50", true)]
    [InlineData("fritz.box", "192.168.178.20", true)]
    [InlineData("desk.home.arpa", "fd00::20", true)]
    [InlineData("desk.localdomain", "172.16.0.9", true)]
    [InlineData("127.0.0.1", null, false)]
    [InlineData("8.8.8.8", null, false)]
    [InlineData("4.3.2.1", null, false)]
    [InlineData("224.0.0.251", null, false)]
    [InlineData("255.255.255.255", null, false)]
    [InlineData("example.com", "93.184.216.34", false)]
    [InlineData("sneaky.lan", "127.0.0.1", false)]
    public async Task OnlyBoardsOnTheLocalNetwork(string host, string? resolvesTo, bool allowed)
    {
        var board = new FakeWledBoard();
        var name = DeviceConfig.SplitHostPort(host).Host;
        var address = resolvesTo is null ? IPAddress.Parse(name) : IPAddress.Parse(resolvesTo);
        var http = new FakeWledHandler(board);
        var context = new DriverContext(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, new HttpClient(http), null, (_, _) => ValueTask.FromResult(address)) { Discovery = Ctx.Discovery() };
        using var setup = new WledSetup(context);
        if (allowed)
        {
            Assert.Equal(30, (await setup.ReadAsync(host, CancellationToken.None))["ledCount"]!.GetValue<int>());
        }
        else
        {
            var error = await Assert.ThrowsAsync<HardwareSetupException>(() => setup.ReadAsync(host, CancellationToken.None));
            Assert.Equal($"{host} isn’t on your local network: only boards there can be set up", error.Message);
            Assert.Empty(board.Requests);
        }
    }

    [Fact]
    public async Task NotAnAddressAtAll()
    {
        using var setup = new WledSetup(Ctx.Create());
        Assert.Equal("bad host! isn’t an address: use the board’s IP address or its name", (await Assert.ThrowsAsync<HardwareSetupException>(() => setup.ReadAsync("bad host!", CancellationToken.None))).Message);
    }

    [Fact]
    public void TheAccessPointAddress_OnlyWhileOnTheAccessPoint()
    {
        Assert.True(WledHosts.IsLanAddress(WledHosts.AccessPoint, () => [IPAddress.Parse("4.3.2.17")]));
        Assert.False(WledHosts.IsLanAddress(WledHosts.AccessPoint, () => [IPAddress.Parse("192.168.1.20")]));
        Assert.False(WledHosts.IsLanAddress(IPAddress.Parse("4.3.2.7"), () => [IPAddress.Parse("4.3.2.17")]));
        Assert.True(WledHosts.IsLanAddress(IPAddress.Parse("169.254.10.1"), () => []));
        Assert.False(WledHosts.IsLanAddress(IPAddress.Parse("::ffff:127.0.0.1"), () => []));
    }

    [Fact]
    public async Task ANameThatResolvesOffTheNetwork_IsRefused()
    {
        var board = new FakeWledBoard();
        var context = new DriverContext(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, new HttpClient(new FakeWledHandler(board)), null, (_, _) => ValueTask.FromResult(IPAddress.Loopback)) { Discovery = Ctx.Discovery() };
        using var setup = new WledSetup(context);
        await Assert.ThrowsAsync<HardwareSetupException>(() => setup.ReadAsync("wled-desk", CancellationToken.None));
        Assert.Empty(board.Requests);
    }

    [Fact]
    public async Task Identify_FlashesTheWholeBoardThreeTimes_ThenPutsItBack()
    {
        var (setup, board) = Create();
        board.State["seg"]!.AsArray().Add((JsonNode)Obj("""{ "id": 1, "on": true, "fx": 2 }"""));
        await setup.IdentifyAsync(Host, CancellationToken.None);

        var states = board.PostedStates;
        Assert.Equal(7, states.Count);
        Assert.Equal(["FFFFFF", "000000", "FFFFFF", "000000", "FFFFFF", "000000"], states.Take(6).Select(s => s["seg"]![0]!["i"]![2]!.GetValue<string>()));
        Assert.All(states.Take(6), s => Assert.Equal("""{"id":1,"on":false}""", s["seg"]![1]!.ToJsonString()));

        // low brightness, even with WLED's limiter set (the supply may not be known yet)
        Assert.All(states.Take(6), s => Assert.Equal(WledSetup.LowBrightness, s["bri"]!.GetValue<int>()));
        var restored = states[6];
        Assert.Equal(9, restored["seg"]![0]!["fx"]!.GetValue<int>());
        Assert.True(restored["seg"]![1]!["on"]!.GetValue<bool>());

        board.Info["live"] = true;
        await Assert.ThrowsAsync<HardwareSetupException>(() => setup.IdentifyAsync(Host, CancellationToken.None));
    }

    [Fact]
    public async Task Probe_TriesEveryFreePinThenPutsTheSettingsBack()
    {
        var (setup, board) = Create();
        board.Info["arch"] = "ESP32-D0WD-V3";

        // a button on GPIO 0 (never tried anyway), IR on 17, I2C on 21 and 22
        board.Cfg["hw"]!["btn"] = Obj("""{ "ins": [ { "type": 2, "pin": [0] }, { "type": 0, "pin": [4] } ] }""");
        board.Cfg["hw"]!["ir"] = Obj("""{ "pin": 17, "type": 1 }""");
        board.Cfg["hw"]!["if"] = Obj("""{ "i2c-pin": [21, 22], "spi-pin": [-1, -1, -1] }""");
        var original = board.Cfg["hw"]!["led"]!.ToJsonString();

        var candidates = await setup.ProbeStartAsync(Host, 1, CancellationToken.None);

        Assert.Equal([(0, 16), (1, 2), (2, 4), (3, 5), (4, 18), (5, 19)], candidates);
        var ins = board.Cfg["hw"]!["led"]!["ins"]!.AsArray();
        Assert.Equal([16, 2, 4, 5, 18, 19], ins.Select(o => o!["pin"]![0]!.GetValue<int>()));
        Assert.All(ins, o => Assert.Equal((22, 300), (o!["type"]!.GetValue<int>(), o["len"]!.GetValue<int>())));
        Assert.Equal([0, 300, 600, 900, 1200, 1500], ins.Select(o => o!["start"]!.GetValue<int>()));

        // the limit stays (split over the test outputs), and so do the name and Wi-Fi
        Assert.Equal(850, board.Cfg["hw"]!["led"]!["maxpwr"]!.GetValue<int>());
        Assert.Equal(850, ins.Sum(o => o!["maxpwr"]!.GetValue<int>()));
        Assert.Equal(1, board.Restarts);

        // the first LEDs of each white at low brightness, the rest black (no colors left from before)
        Assert.Equal(
            """[0,1800,"000000",0,20,"FFFFFF",300,320,"FFFFFF",600,620,"FFFFFF",900,920,"FFFFFF",1200,1220,"FFFFFF",1500,1520,"FFFFFF"]""",
            board.PostedStates[^1]["seg"]![0]!["i"]!.ToJsonString());
        Assert.Equal(WledSetup.LowBrightness, board.PostedStates[^1]["bri"]!.GetValue<int>());

        // the board starts dark after the restart
        Assert.False(board.Cfg["def"]!["on"]!.GetValue<bool>());

        // round 2: the other pins; the settings saved before round 1 are the ones restored
        var second = await setup.ProbeStartAsync(Host, 2, CancellationToken.None);
        Assert.Equal([3, 13, 14, 15, 23, 25, 26, 27, 32, 33], second.Select(c => c.Pin));

        Assert.True(await setup.ProbeEndAsync(Host, keep: false, CancellationToken.None));
        Assert.Equal(original, board.Cfg["hw"]!["led"]!.ToJsonString());
        Assert.Equal(3, board.Restarts);
        Assert.False(await setup.ProbeEndAsync(Host, keep: false, CancellationToken.None));
    }

    [Fact]
    public async Task Probe_KeepForgetsTheSavedSettings_AndTheyExpireLikeTheChecks()
    {
        var time = new FakeTimeProvider();
        var (setup, board) = Create(time);
        await setup.ProbeStartAsync(Host, 1, CancellationToken.None);
        Assert.False(await setup.ProbeEndAsync(Host, keep: true, CancellationToken.None));
        Assert.Equal(1, board.Restarts);

        // keep only forgets: the found strips' write came first (and turned the test outputs black)
        Assert.Contains(",0,20,\"FFFFFF\",", board.PostedStates[^1]["seg"]![0]!["i"]!.ToJsonString(), StringComparison.Ordinal);

        // each step keeps the saved settings another 30 minutes
        await setup.ProbeStartAsync(Host, 1, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(20));
        await setup.TestAsync(Host, 0, WledTestPattern.White, null, null, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(20));
        Assert.True(await setup.ProbeEndAsync(Host, keep: false, CancellationToken.None));

        await setup.ProbeStartAsync(Host, 1, CancellationToken.None);
        time.Advance(WledSetup.SavedStateLifetime + TimeSpan.FromMinutes(1));
        Assert.False(await setup.ProbeEndAsync(Host, keep: false, CancellationToken.None));
    }

    [Fact]
    public void Points_LightTheOutputDimAndTheGivenLedsBright()
    {
        var board = new FakeWledBoard();
        var lit = WledSetup.TestState(board.Info, board.Cfg, board.State, 0, WledTestPattern.Points, null, null, [30, 1, 12]);
        Assert.Equal("""[0,30,"000000",0,30,"A0A0A0",0,"FFFFFF",11,"FFFFFF",29,"FFFFFF"]""", lit["seg"]![0]!["i"]!.ToJsonString());
        Assert.Equal(WledSetup.LowBrightness, lit["bri"]!.GetValue<int>());

        Assert.Throws<HardwareSetupException>(() => WledSetup.TestState(board.Info, board.Cfg, board.State, 0, WledTestPattern.Points, null, null, [31]));
        Assert.Throws<HardwareSetupException>(() => WledSetup.TestState(board.Info, board.Cfg, board.State, 0, WledTestPattern.Points, null, null, []));
    }

    [Fact]
    public void Pins_PerChip()
    {
        // WLED before 0.15 drives at most 8 digital outputs on an ESP32
        Assert.Equal(8, WledPins.MaxOutputs("ESP32-D0WD-V3", "0.14.4"));
        Assert.Equal(int.MaxValue, WledPins.MaxOutputs("ESP32-D0WD-V3", "0.15.0"));
        Assert.Equal(int.MaxValue, WledPins.MaxOutputs("ESP32-D0WD-V3", "16.0.1"));
        Assert.Equal(int.MaxValue, WledPins.MaxOutputs("esp8266", "0.14.4"));

        Assert.Equal("esp32", WledPins.Family("ESP32-D0WD-V3"));
        Assert.Equal("esp32-s3", WledPins.Family("ESP32-S3"));
        Assert.Equal("esp32-s2", WledPins.Family("ESP32-S2"));
        Assert.Equal("esp32-c3", WledPins.Family("ESP32-C3"));
        Assert.Equal("esp8266", WledPins.Family("esp8266"));
        Assert.Equal("esp32", WledPins.Family(null));
        Assert.Equal([2, 3], WledPins.Candidates("esp8266", 1));
        foreach (var chip in new[] { "esp32", "esp8266", "ESP32-S2", "ESP32-S3", "ESP32-C3" })
        {
            var all = WledPins.Candidates(chip, 1).Concat(WledPins.Candidates(chip, 2)).ToList();
            Assert.Equal(all.Count, all.Distinct().Count());
            Assert.DoesNotContain(0, all);
        }

        Assert.DoesNotContain(WledPins.Candidates("esp32", 2), p => p is >= 6 and <= 11 or >= 34);
        Assert.DoesNotContain(WledPins.Candidates("ESP32-S3", 2).Concat(WledPins.Candidates("ESP32-S3", 1)), p => p is 19 or 20 or >= 26);
    }
    [Fact]
    public void StripPresets_MapToWledTypes()
    {
        Assert.Equal(22, WledStrips.Find("WS2812B")!.WledType);
        Assert.Equal(30, WledStrips.Find("ws2814-rgbw")!.WledType);
        Assert.True(WledStrips.Find("apa102")!.ClockPin);
        Assert.Equal(["GRB", "RGB", "BRG", "RBG", "BGR", "GBR"], WledStrips.ColorOrders);
        Assert.All(WledStrips.All, s => Assert.True(WledStrips.OrderNumber(s.ColorOrder) >= 0));
        Assert.Equal(WledStrips.All.Count, WledStrips.All.Select(s => s.Id).Distinct().Count());
        Assert.Equal("ws2812b", WledStrips.ForType(22, 55)!.Id);
        Assert.Equal("ws2815", WledStrips.ForType(22, 255)!.Id);
        Assert.Null(WledStrips.ForType(41, 0));
        Assert.Equal((22, 22, 30), (WledStrips.Find("ws2811-cob")!.WledType, WledStrips.Find("ws2812-cob")!.WledType, WledStrips.Find("ws2814-cob-rgbw")!.WledType));
    }
}
