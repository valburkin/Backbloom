using System.Diagnostics;
using System.Numerics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Backbloom.Audio;
using Backbloom.Capture;
using Backbloom.Core.Color;
using Backbloom.Core.Effects;
using Backbloom.Core.Imaging;
using Backbloom.Core.Layout;
using Backbloom.Core.Mapping;
using Backbloom.Core.Priorities;
using Backbloom.Core.Smoothing;
using Backbloom.Devices;
using Backbloom.Engine.Platform;
using Backbloom.Settings;
using Backbloom.Settings.Layout;
using Backbloom.Settings.Storage;

namespace Backbloom.Engine.Tests;

// the allocation tests measure on their own thread, without the parallel tests' load (see Allocations)
[Collection(SerialTests.Name)]
public sealed class ZoneTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"backbloom-zones-{Guid.NewGuid():N}");
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        _owned.ForEach(d => d.Dispose());
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    // Controller "c": the strip "top" (LEDs 0..9) along the screen's top edge; the strip "side" (LEDs 10..21) down its
    // right edge, red on its first half (10..15) and playing an effect on its second (16..21). The profile "game" turns
    // the top strip into the same effect and the side's first half blue; its second half shows the screen.
    internal static string Layout(string effect = "Rainbow swirl", string zoneColor = "[255, 0, 0]") => $$"""
        { "version": 1,
          "room": { "width": 4, "depth": 4, "height": 2.6, "screen": { "x": 1.7, "y": 1.31, "width": 0.6, "height": 0.34 }, "furniture": "desk" },
          "controllers": [{ "id": "c", "name": "C", "outputs": [{ "index": 0, "start": 0, "length": 10 }, { "index": 1, "start": 10, "length": 12 }],
                            "device": { "type": "file", "output": "\\\\.\\NUL", "printTimeStamp": false } }],
          "strips": [
            { "id": "top", "name": "Top", "controllerId": "c", "output": 0, "ledCount": 10,
              "path": [{ "surface": "front", "x": 1.7, "y": 1.31 }, { "surface": "front", "x": 2.3, "y": 1.31 }] },
            { "id": "side", "name": "Side", "controllerId": "c", "output": 1, "ledCount": 12,
              "path": [{ "surface": "front", "x": 2.3, "y": 1.31, "next": { "kind": "color", "color": {{zoneColor}} } },
                       { "surface": "front", "x": 2.3, "y": 1.48, "next": { "kind": "effect", "effect": "{{effect}}" } },
                       { "surface": "front", "x": 2.3, "y": 1.65 }] } ],
          "profiles": [{ "id": "game", "name": "Game", "apps": ["game.exe"],
                         "sections": { "top": [{ "kind": "effect", "effect": "{{effect}}" }], "side": [{ "kind": "color", "color": [0, 0, 255] }, null] } }] }
        """;

    private static void WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException();
            }

            Thread.Sleep(10);
        }
    }

    private SqliteSettingsStore Store(string? layout = null)
    {
        var registry = new DriverRegistry().RegisterAll();
        var database = SettingsDatabase.Open(Path.Combine(_dir, SettingsDatabase.FileName));
        _owned.Add(database);
        var store = new SqliteSettingsStore(database, SettingsSchema.CreateBuiltin(registry.Drivers.ToDictionary(d => d.Type, d => d.Schema)), NullLogger<SqliteSettingsStore>.Instance);
        var foreground = store.GetSection("foregroundEffect")!.AsObject();
        foreground["enable"] = false;
        var smoothing = store.GetSection("smoothing")!.AsObject();
        smoothing["enable"] = false;
        var result = store.Update(new JsonObject
        {
            ["foregroundEffect"] = foreground.DeepClone(),
            ["smoothing"] = smoothing.DeepClone(),
            ["layout"] = JsonNode.Parse(layout ?? Layout()),
        });
        Assert.True(result.Success, string.Join("; ", result.Errors));
        return store;
    }

    private LightingEngine Engine(ISettingsStore store, IFrameSource source, SoundCapture? sound = null)
    {
        var registry = new DriverRegistry().RegisterAll();
        var devices = new DeviceManager(registry, new DriverContext(NullLoggerFactory.Instance, new HttpClient()), NullLoggerFactory.Instance);
        var engine = new LightingEngine(store, source, devices, new SystemClock(), NullLoggerFactory.Instance, sound);
        _owned.Insert(0, engine);
        return engine;
    }

    private static (int R, int G, int B)[] Preview(LightingEngine engine)
    {
        var bytes = new byte[engine.Layout.Count * 3];
        var n = engine.LedColors.CopyLatest(bytes);
        return [.. Enumerable.Range(0, n / 3).Select(i => ((int)bytes[i * 3], (int)bytes[(i * 3) + 1], (int)bytes[(i * 3) + 2]))];
    }

    // black after the color settings (the minimal backlight keeps LEDs at 1 of 255, as for the screen's black)
    private static bool IsBlack((int R, int G, int B) c) => c.R <= 1 && c.G <= 1 && c.B <= 1;

    private static bool IsRed((int R, int G, int B) c) => c.R > 250 && c.G < 5 && c.B < 5;

    private static bool IsGreen((int R, int G, int B) c) => c.G > 250 && c.R < 5 && c.B < 5;

    [Fact]
    public void Zones_PlayTheirEffectAndColorOverTheScreen()
    {
        var engine = Engine(Store(), new SolidSource(0, 255, 0));
        engine.Start();

        Assert.Equal([ZoneRole.OfColor(255, 0, 0), ZoneRole.OfEffect("Rainbow swirl")], engine.Zones.Roles);
        WaitUntil(() => Preview(engine) is { Length: 22 } p && p.Take(10).All(IsGreen) && p.Skip(10).Take(6).All(IsRed));

        // the effect zone animates on its own (the swirl turns), never showing the screen's green
        var first = Preview(engine).Skip(16).ToArray();
        WaitUntil(() => !Preview(engine).Skip(16).SequenceEqual(first));
        Assert.DoesNotContain(Preview(engine).Skip(16), IsGreen);
    }

    [Fact]
    public void Zones_PlayOverTheBackgroundEffect_NotOverAClientsEffect()
    {
        // the idle effect fills only what the layout leaves to it: a setup playing an effect on its strips keeps showing
        Assert.True(LightingPipeline.ShowsLayout(new VisibleSource(EffectEngine.BackgroundPriority, SourceComponent.Effect, default, null, 0, 1)));
        Assert.True(LightingPipeline.ShowsLayout(VisibleSource.None));
        Assert.False(LightingPipeline.ShowsLayout(new VisibleSource(1, SourceComponent.Effect, default, null, 0, 1)));
        Assert.False(LightingPipeline.ShowsLayout(new VisibleSource(EffectEngine.ForegroundPriority, SourceComponent.Effect, default, null, 0, 1)));
    }

    [Fact]
    public void Zones_KeepTheControllersOnAndAnimating_WithoutASource()
    {
        var engine = Engine(Store(), new SolidSource(0, 255, 0));
        engine.Start();
        engine.Components.Set(Component.SystemGrabber, false);

        // nothing is visible: the screen's LEDs are black, the zones play, the controller stays on
        Assert.False(engine.Sources.Visible.HasSource);
        WaitUntil(() => Preview(engine) is { Length: 22 } p && p.Take(10).All(c => c == (0, 0, 0)) && p.Skip(10).Take(6).All(IsRed));
        var frames = engine.LedColors.Sequence;
        var first = Preview(engine).Skip(16).ToArray();
        WaitUntil(() => !Preview(engine).Skip(16).SequenceEqual(first) && engine.LedColors.Sequence > frames + 20);
        WaitUntil(() => engine.Devices.Single().State == DeviceState.Ready);

        // the output held (HyperHDR runs): the zone effects pause
        engine.Components.SetHeld(true);
        WaitUntil(() => engine.Devices.Single().State == DeviceState.Off);
        var effect = engine.Zones.Effects["Rainbow swirl"];
        Thread.Sleep(50);
        var paused = effect.NextStepAt;
        Thread.Sleep(200);
        Assert.Equal(paused, effect.NextStepAt);
        engine.Components.SetHeld(false);
        WaitUntil(() => effect.NextStepAt != paused && engine.Devices.Single().State == DeviceState.Ready);
    }

    [Fact]
    public void AnEffectForEveryLight_PlaysOnTheZonesToo_WhichPauseUntilTheLayoutShowsAgain()
    {
        var engine = Engine(Store(), new SolidSource(0, 255, 0));
        engine.Start();
        var swirl = engine.Zones.Effects["Rainbow swirl"];
        WaitUntil(() => Preview(engine) is { Length: 22 } p && p.Skip(10).Take(6).All(IsRed) && swirl.NextStepAt > 0);

        // a color for every light: the zones' LEDs take it too, and their effect stops stepping
        engine.SetColor(50, new Rgb8(0, 0, 255), -1, "test");
        WaitUntil(() => Preview(engine) is { Length: 22 } p && p.All(c => c.B > 250 && c.R < 5 && c.G < 5));
        var paused = swirl.NextStepAt;
        Thread.Sleep(200);
        Assert.Equal(paused, swirl.NextStepAt);

        // an effect: it plays on the zones' LEDs (mapped as 'auto' maps them), not their own color
        Assert.Equal(EffectStart.Started, engine.RunEffect("Candle", 50, -1, "test"));
        WaitUntil(() => Preview(engine) is { Length: 22 } p && !p.Skip(10).Take(6).Any(IsRed) && p.Skip(10).Any(c => !IsBlack(c)));

        // the screen again: the zones play
        Assert.True(engine.Clear(50));
        WaitUntil(() => Preview(engine) is { Length: 22 } p && p.Take(10).All(IsGreen) && p.Skip(10).Take(6).All(IsRed) && swirl.NextStepAt != paused);
    }

    [Fact]
    public void EffectSource_ReturnsForTheWakeTime_AndCatchesUpWhenStepped()
    {
        var clock = new SteppingClock(start: 1000);
        using var source = new EffectSource(EffectCatalog.Find("Candle")!, 50, -1, clock, MsvcRandom.CreateSeeded(), null);
        var mapping = new LedMappingOptions(LedLayout.FromJson(JsonNode.Parse("""[{ "hmin": 0, "hmax": 1, "vmin": 0, "vmax": 1 }, { "hmin": 0, "hmax": 1, "vmin": 0, "vmax": 1 }]""")));
        var colors = new Vector3[2];

        // the first step at once, the next 100 ms later; a wake time before that ends the wait there
        Assert.True(source.TryRender(mapping, colors, CancellationToken.None));
        Assert.Equal(1100, source.NextStepAt);
        Assert.False(source.TryRender(mapping, colors, 1030, CancellationToken.None));
        Assert.Equal(1030, clock.NowMilliseconds);
        Assert.True(source.TryRender(mapping, colors, 1300, CancellationToken.None));
        Assert.Equal((1100, 1200), (clock.NowMilliseconds, source.NextStepAt));

        // stepped without waiting: nothing before it is due, then every missed step at once (a zone stepped with the
        // screen's frames keeps its speed), and the clock isn't waited on
        Assert.False(source.TryStep(mapping, colors, 1150));
        Assert.True(source.TryStep(mapping, colors, 1250));
        Assert.Equal(1300, source.NextStepAt);
        using var fast = new EffectSource(EffectCatalog.Find("Rainbow swirl fast")!, 50, -1, clock, MsvcRandom.CreateSeeded(), null);
        Assert.True(fast.TryStep(mapping, colors, 2000));
        var step = fast.NextStepAt - 2000;
        Assert.InRange(step, 5, 30);
        Assert.False(fast.TryStep(mapping, colors, 2000 + step - 1));
        Assert.True(fast.TryStep(mapping, colors, 2000 + (3 * step) + 1));
        Assert.Equal(2000 + (4 * step), fast.NextStepAt);
        Assert.Equal(1100, clock.NowMilliseconds);
    }

    [Fact]
    public void WithoutZones_NoSourceSwitchesTheControllersOff()
    {
        var store = Store(Layout().Replace("""{ "kind": "color", "color": [255, 0, 0] }""", """{ "kind": "none" }""", StringComparison.Ordinal)
            .Replace("""{ "kind": "effect", "effect": "Rainbow swirl" }""", """{ "kind": "whole" }""", StringComparison.Ordinal));
        var engine = Engine(store, new SolidSource(0, 255, 0));
        engine.Start();
        Assert.Equal(0, engine.Zones.Count);
        WaitUntil(() => engine.Devices.Single().State == DeviceState.Ready);

        engine.Components.Set(Component.SystemGrabber, false);
        WaitUntil(() => engine.Devices.Single().State == DeviceState.Off);
    }

    [Fact]
    public void AProfile_SwitchesTheZones_KeepingTheControllerAndTheRunningEffect()
    {
        var engine = Engine(Store(), new SolidSource(0, 255, 0));
        engine.Start();
        WaitUntil(() => engine.Devices.Single().State == DeviceState.Ready && engine.Devices.Single().Stats.Written > 5);
        var swirl = engine.Zones.Effects["Rainbow swirl"];
        var written = engine.Devices.Single().Stats.Written;

        engine.ApplyProfile("game");

        // the top strip plays the effect (the same instance, now over both strips), the side's first half is blue and
        // its second half shows the screen; the controller wasn't reconnected
        Assert.Equal("game", engine.ProfileId);
        Assert.Equal([ZoneRole.OfEffect("Rainbow swirl"), ZoneRole.OfColor(0, 0, 255)], engine.Zones.Roles);
        Assert.Same(swirl, engine.Zones.Effects["Rainbow swirl"]);
        WaitUntil(() => Preview(engine) is { Length: 22 } p && p.Skip(10).Take(6).All(c => c.B > 250 && c.R < 5) && p.Skip(16).All(IsGreen));
        Assert.True(engine.Devices.Single().Stats.Written >= written);
        Assert.Equal(22, engine.Layout.Count);

        // back to the layout as saved
        engine.ApplyProfile(null);
        Assert.Null(engine.ProfileId);
        Assert.Same(swirl, engine.Zones.Effects["Rainbow swirl"]);
        WaitUntil(() => Preview(engine) is { Length: 22 } p && p.Take(10).All(IsGreen) && p.Skip(10).Take(6).All(IsRed));

        // a profile the layout doesn't have runs the layout as saved
        engine.ApplyProfile("nope");
        Assert.Null(engine.ProfileId);
        Assert.Equal(2, engine.Zones.Count);
    }

    [Fact]
    public void ALayoutChange_RestartsOnlyTheZonesWhoseRoleChanged()
    {
        var store = Store();
        var engine = Engine(store, new SolidSource(0, 255, 0));
        engine.Start();
        var swirl = engine.Zones.Effects["Rainbow swirl"];

        Assert.True(store.Update(new JsonObject { ["layout"] = JsonNode.Parse(Layout(zoneColor: "[0, 255, 255]")) }).Success);
        Assert.Same(swirl, engine.Zones.Effects["Rainbow swirl"]);
        WaitUntil(() => Preview(engine).Skip(10).Take(6).All(c => c.R < 5 && c.G > 250 && c.B > 250));

        Assert.True(store.Update(new JsonObject { ["layout"] = JsonNode.Parse(Layout(effect: "Candle")) }).Success);
        Assert.Equal("Candle", Assert.Single(engine.Zones.Effects.Keys));
    }

    [Fact]
    public void AnUnknownEffect_LeavesItsZoneDark()
    {
        var engine = Engine(Store(Layout(effect: "No such effect")), new SolidSource(0, 255, 0));
        engine.Start();

        Assert.Equal(2, engine.Zones.Count);
        Assert.Empty(engine.Zones.Effects);
        WaitUntil(() => Preview(engine) is { Length: 22 } p && p.Take(10).All(IsGreen) && p.Skip(16).All(IsBlack));
    }

    [Fact]
    public void AMusicZone_HoldsTheSoundCapture_AndStaysDarkWithoutIt()
    {
        var platform = new FakeAudioCapture();
        var sound = new SoundCapture(platform, NullLogger<SoundCapture>.Instance);
        _owned.Add(sound);
        var store = Store(Layout(effect: MusicPulseEffect.Name));
        Assert.True(store.Update(JsonNode.Parse("""{ "soundEffect": { "enable": false, "device": "default", "enable_smoothing": true } }""")!.AsObject()).Success);
        var engine = Engine(store, new SolidSource(0, 255, 0), sound);
        engine.Start();

        // sound capture off: dark
        Assert.Equal(0, platform.Started);
        Thread.Sleep(100);
        Assert.All(Preview(engine).Skip(16), c => Assert.True(IsBlack(c)));

        // on: the zone opens the capture and lights up with the music
        Assert.True(store.Update(JsonNode.Parse("""{ "soundEffect": { "enable": true, "device": "default", "enable_smoothing": true } }""")!.AsObject()).Success);
        WaitUntil(() => platform.Running == 1 && Preview(engine).Skip(16).Any(c => c.R + c.G + c.B > 30));

        // the zone goes: so does the capture
        Assert.True(store.Update(new JsonObject { ["layout"] = JsonNode.Parse(Layout(effect: "Candle")) }).Success);
        WaitUntil(() => platform.Running == 0);
    }

    // Two effect zones (a picture and LED colors) and a color zone over 168 + 30 LEDs.
    private static (LedLayout Layout, ZoneLayer Zones) AllocationSetup(IClock clock)
    {
        var areas = LedLayout.FromJson(JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "layouts", "owner-168.json")))).Areas.ToList();
        areas.AddRange(Enumerable.Repeat(new LedArea(0.5, 0.5, 0.5, 0.5, 0, Disabled: true), 30));
        JsonArray Rects(int n) => [.. Enumerable.Range(0, n).Select(i => (JsonNode)new JsonObject { ["hmin"] = i / (double)n, ["hmax"] = (i + 1) / (double)n, ["vmin"] = 0, ["vmax"] = 0.1 })];
        LayoutZone[] zones =
        [
            new(ZoneRole.OfEffect("Rainbow swirl"), [.. Enumerable.Range(168, 12)], Rects(12)),
            new(ZoneRole.OfEffect("Candle"), [.. Enumerable.Range(180, 12)], Rects(12)),
            new(ZoneRole.OfColor(255, 120, 0), [.. Enumerable.Range(192, 6)], Rects(6)),
        ];
        var layer = ZoneLayer.Create(zones, null, d => new EffectSource(d, -1, -1, clock, MsvcRandom.CreateSeeded(), null), _ => { }, out _);
        return (new LedLayout(areas), layer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZonesOverTheScreen_DoNotAllocate(bool smoothing)
    {
        var registry = new DriverRegistry().RegisterAll();
        using var devices = new DeviceManager(registry, new DriverContext(NullLoggerFactory.Instance, new HttpClient()), NullLoggerFactory.Instance);
        devices.Configure([new ControllerDevice("tv", "TV", JsonNode.Parse("""{ "type": "file", "output": "/dev/null", "printTimeStamp": true }""")!.AsObject(), 198)]);
        devices.SetEnabled(true);
        var clock = new ManualClock();
        var (layout, zones) = AllocationSetup(clock);
        using var _ = zones;
        using var output = new OutputStage(devices, new LedColorsFeed(), clock, NullLogger<OutputStage>.Instance);
        output.Configure(SmoothingSettings.FromJson(JsonNode.Parse($$"""{ "enable": {{(smoothing ? "true" : "false")}}, "type": "HybridInterpolator", "updateFrequency": 50, "continuousOutput": true }""")));
        var screen = new VisibleSource(245, SourceComponent.SystemGrabber, Rgb8.Black, null, 0, 1);
        using var pipeline = new LightingPipeline(
            new SyntheticFrameSource(new SyntheticFrameSourceOptions(FramesPerSecond: 100_000)),
            new ComponentStates(),
            () => screen,
            output,
            new PipelineConfig(new LedMappingOptions(layout), new ColorProcessor(new ColorAdjustment { ScaleOutput = 0.8f })),
            NullLogger<LightingPipeline>.Instance,
            clock: clock);
        pipeline.ConfigureZones(zones);
        Frame? frame = null;
        var colors = Array.Empty<Vector3>();
        var token = TestContext.Current.CancellationToken;
        void Step()
        {
            pipeline.RunOnce(ref frame, ref colors, token);
            clock.Advance(20);
            output.Tick(clock.NowMilliseconds);
        }

        long due = 0;
        var allocated = Allocations.Measure(
            () =>
            {
                for (var i = 0; i < 5; i++)
                {
                    Step();
                }

                due = zones.NextDue;
            },
            () =>
            {
                for (var i = 0; i < 500; i++)
                {
                    Step();
                }
            });

        Assert.Equal(0, allocated);
        Assert.True(zones.NextDue > due + 9000, "the zone effects stepped with the frames");
    }

    [Fact]
    public void ZonesWithoutASource_WakeTheLoop_WithoutAllocating()
    {
        var registry = new DriverRegistry().RegisterAll();
        using var devices = new DeviceManager(registry, new DriverContext(NullLoggerFactory.Instance, new HttpClient()), NullLoggerFactory.Instance);
        devices.Configure([new ControllerDevice("tv", "TV", JsonNode.Parse("""{ "type": "file", "output": "/dev/null", "printTimeStamp": true }""")!.AsObject(), 198)]);
        devices.SetEnabled(true);
        var clock = new ManualClock();
        var (layout, zones) = AllocationSetup(clock);
        using var _ = zones;
        var feed = new LedColorsFeed();
        using var output = new OutputStage(devices, feed, clock, NullLogger<OutputStage>.Instance);
        output.Configure(SmoothingSettings.FromJson(JsonNode.Parse("""{ "enable": false }""")));
        using var pipeline = new LightingPipeline(
            new SyntheticFrameSource(new SyntheticFrameSourceOptions(FramesPerSecond: 100_000)),
            new ComponentStates(),
            () => VisibleSource.None,
            output,
            new PipelineConfig(new LedMappingOptions(layout), new ColorProcessor(ColorAdjustment.Default)),
            NullLogger<LightingPipeline>.Instance,
            clock: clock);
        pipeline.ConfigureZones(zones);
        Frame? frame = null;
        var colors = Array.Empty<Vector3>();
        var token = TestContext.Current.CancellationToken;
        void Step()
        {
            pipeline.RunOnce(ref frame, ref colors, token);
            clock.Advance(5);
        }

        long sent = 0;
        var allocated = Allocations.Measure(
            () =>
            {
                for (var i = 0; i < 20; i++)
                {
                    Step();
                }

                sent = feed.Sequence;
            },
            () =>
            {
                for (var i = 0; i < 2000; i++)
                {
                    Step();
                }
            });

        Assert.Equal(0, allocated);

        // black drawn once; then a frame per zone step (the swirl steps every 56 ms, the candle every 100 ms)
        Assert.Equal(1, pipeline.FrameCount);
        Assert.InRange(feed.Sequence - sent, 150, 300);
    }
}
