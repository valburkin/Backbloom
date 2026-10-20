using System.Diagnostics;
using System.Numerics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Overglow.Capture;
using Overglow.Core.Color;
using Overglow.Core.Imaging;
using Overglow.Core.Layout;
using Overglow.Core.Mapping;
using Overglow.Core.Priorities;
using Overglow.Core.Smoothing;
using Overglow.Devices;
using Overglow.Engine.Platform;
using Overglow.Settings;
using Overglow.Settings.Storage;

namespace Overglow.Engine.Tests;

/// <summary>Green picture with black bars of a sixth of the height at the top and the bottom.</summary>
internal sealed class LetterboxSource : IFrameSource
{
    public string Name => "Letterbox";

    public string Device => "test";

    public (int Width, int Height) FrameSize => (64, 36);

    public bool TryCapture(Frame frame, CancellationToken cancellationToken)
    {
        if (cancellationToken.WaitHandle.WaitOne(5))
        {
            return false;
        }

        var px = frame.Pixels;
        for (var y = 0; y < frame.Height; y++)
        {
            var bar = y < 6 || y >= frame.Height - 6;
            for (var x = 0; x < frame.Width; x++)
            {
                var o = (y * frame.Stride) + (x * 4);
                px[o] = 0;
                px[o + 1] = bar ? (byte)0 : (byte)255;
                px[o + 2] = 0;
                px[o + 3] = 255;
            }
        }

        return true;
    }

    public void Dispose()
    {
    }
}

/// <summary>A frame source that fills every frame with one color, as fast as asked.</summary>
internal sealed class SolidSource(byte r, byte g, byte b, int width = 64, int height = 36) : IFrameSource
{
    public string Name => "Solid";

    public string Device => "test";

    public (int Width, int Height) FrameSize => (width, height);

    public bool TryCapture(Frame frame, CancellationToken cancellationToken)
    {
        if (cancellationToken.WaitHandle.WaitOne(5))
        {
            return false;
        }

        var px = frame.Pixels;
        for (var i = 0; i < px.Length; i += 4)
        {
            px[i] = b;
            px[i + 1] = g;
            px[i + 2] = r;
            px[i + 3] = 255;
        }

        return true;
    }

    public void Dispose()
    {
    }
}

public sealed class EngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"overglow-engine-{Guid.NewGuid():N}");
    private readonly List<SettingsDatabase> _databases = [];

    public void Dispose()
    {
        _databases.ForEach(d => d.Dispose());
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static DriverRegistry Registry() => new DriverRegistry().RegisterAll();

    private SqliteSettingsStore Store(DriverRegistry registry)
    {
        var database = SettingsDatabase.Open(Path.Combine(_dir, SettingsDatabase.FileName));
        _databases.Add(database);
        var store = new SqliteSettingsStore(database, SettingsSchema.CreateBuiltin(registry.Drivers.ToDictionary(d => d.Type, d => d.Schema)), NullLogger<SqliteSettingsStore>.Instance);

        // the startup effect (on by default) would cover the screen for 3 s
        var foreground = store.GetSection("foregroundEffect")!.AsObject();
        foreground["enable"] = false;
        Assert.True(store.Update(new JsonObject { ["foregroundEffect"] = foreground.DeepClone() }).Success);
        return store;
    }

    private static DeviceManager Devices(DriverRegistry registry) =>
        new(registry, new DriverContext(NullLoggerFactory.Instance, new HttpClient()), NullLoggerFactory.Instance);

    private static DeviceInfo? FirstDevice(LightingEngine engine) => engine.Devices is [var d, ..] ? d : null;

    private static void WaitUntil(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(5))
            {
                throw new TimeoutException();
            }

            Thread.Sleep(10);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pipeline_SteadyStateDoesNotAllocate(bool smoothing)
    {
        var registry = Registry();
        using var devices = Devices(registry);
        devices.Configure(JsonNode.Parse("""{ "type": "file", "output": "/dev/null", "printTimeStamp": true }""")!.AsObject(), 168);
        devices.SetEnabled(true);
        var layout = LedLayout.FromJson(JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "layouts", "owner-168.json"))));
        var config = new PipelineConfig(new LedMappingOptions(layout), new ColorProcessor(new ColorAdjustment { ScaleOutput = 0.8f }));
        var clock = new ManualClock();
        using var output = new OutputStage(devices, new LedColorsFeed(), clock, NullLogger<OutputStage>.Instance);
        output.Configure(SmoothingSettings.FromJson(JsonNode.Parse($$"""{ "enable": {{(smoothing ? "true" : "false")}}, "type": "HybridInterpolator", "updateFrequency": 50, "continuousOutput": true }""")));
        var screen = new VisibleSource(245, SourceComponent.SystemGrabber, Rgb8.Black, null, 0, 1);
        using var pipeline = new LightingPipeline(
            new SyntheticFrameSource(new SyntheticFrameSourceOptions(FramesPerSecond: 100_000)),
            new ComponentStates(),
            () => screen,
            output,
            config,
            NullLogger<LightingPipeline>.Instance);
        Frame? frame = null;
        var colors = Array.Empty<Vector3>();
        var token = TestContext.Current.CancellationToken;
        var sent = 0;
        void Step()
        {
            pipeline.RunOnce(ref frame, ref colors, token);
            clock.Advance(20);
            sent += output.Tick(clock.NowMilliseconds) ? 1 : 0;
        }

        for (var i = 0; i < 5; i++)
        {
            Step();
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 500; i++)
        {
            Step();
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(505, pipeline.FrameCount);
        Assert.Equal(smoothing ? 505 : 0, sent);
    }

    [Fact]
    public void Engine_DrivesTheFileDeviceWithProcessedColors()
    {
        var registry = Registry();
        var store = Store(registry);
        var output = Path.Combine(_dir, "leds.txt");
        var result = store.Update(JsonNode.Parse($$"""
            { "leds": [{ "hmin": 0, "hmax": 0.5, "vmin": 0, "vmax": 1 }, { "hmin": 0.5, "hmax": 1, "vmin": 0, "vmax": 1 }],
              "device": { "type": "file", "output": {{JsonValue.Create(output).ToJsonString()}}, "printTimeStamp": false, "colorOrder": "grb" } }
            """)!.AsObject());
        Assert.True(result.Success, string.Join("; ", result.Errors));

        using (var engine = new LightingEngine(store, new SolidSource(255, 0, 0), Devices(registry), new SystemClock(), NullLoggerFactory.Instance))
        {
            engine.Start();
            WaitUntil(() => FirstDevice(engine)?.Stats.Written > 3);
            var preview = new byte[6];
            Assert.Equal(6, engine.LedColors.CopyLatest(preview));
            // The default minimal backlight (0.0039) lifts the other channels to 1.
            Assert.Equal(new byte[] { 255, 1, 1, 255, 1, 1 }, preview);
        }

        var lines = File.ReadAllLines(output);
        Assert.Contains(" [{1,255,1}{1,255,1}]", lines);
        Assert.Equal(" [{0,0,0}{0,0,0}]", lines[^1]);
    }

    [Fact]
    public void Engine_AppliesSettingChangesAndRaisesEvents()
    {
        var registry = Registry();
        var store = Store(registry);
        using var engine = new LightingEngine(store, new SolidSource(10, 20, 30), Devices(registry), new SystemClock(), NullLoggerFactory.Instance);
        engine.Start();
        JsonArray? adjustment = null;
        JsonArray? leds = null;
        string? mapping = null;
        engine.AdjustmentChanged += a => adjustment = a;
        engine.LedsChanged += l => leds = l;
        engine.MappingTypeChanged += m => mapping = m;

        var color = store.GetSection("color")!.AsObject();
        color["imageToLedMappingType"] = "unicolor_mean";
        color["channelAdjustment"]![0]!["scaleOutput"] = 0.5;
        Assert.True(store.Update(new JsonObject { ["color"] = color }).Success);
        Assert.True(store.Update(JsonNode.Parse("""{ "leds": [{ "hmin": 0, "hmax": 1, "vmin": 0, "vmax": 1 }, { "hmin": 0, "hmax": 1, "vmin": 0, "vmax": 1 }] }""")!.AsObject()).Success);

        Assert.Equal(0.5, adjustment![0]!["scaleOutput"]!.GetValue<double>());
        Assert.Equal("unicolor_mean", mapping);
        Assert.Equal(2, leds!.Count);
        Assert.Equal(2, engine.Layout.Count);
        Assert.Equal(LedMappingType.UnicolorMean, engine.MappingType);
        WaitUntil(() => engine.LedColors.LedCount == 2);
    }

    [Fact]
    public void Engine_BlackBorderDetectionFollowsTheSettingAndTheComponent()
    {
        var registry = Registry();
        var store = Store(registry);
        Assert.True(store.Update(JsonNode.Parse("""
            { "leds": [{ "hmin": 0, "hmax": 1, "vmin": 0, "vmax": 0.1 }],
              "blackborderdetector": { "enable": true, "threshold": 5, "unknownFrameCnt": 600, "borderFrameCnt": 5, "maxInconsistentCnt": 2, "blurRemoveCnt": 1, "mode": "default" } }
            """)!.AsObject()).Success);
        using var engine = new LightingEngine(store, new LetterboxSource(), Devices(registry), new SystemClock(), NullLoggerFactory.Instance);
        Assert.True(engine.Components.IsOn(Component.BlackBorder));
        engine.Start();
        var preview = new byte[3];

        // the top LED sees the picture inside the bars
        WaitUntil(() => engine.LedColors.CopyLatest(preview) == 3 && preview[1] > 200);

        engine.Components.Set(Component.BlackBorder, false);
        WaitUntil(() => engine.LedColors.CopyLatest(preview) == 3 && preview[1] < 5);
    }

    [Fact]
    public void Engine_SmoothsAStaticColorChangeAtTheUpdateFrequency()
    {
        var registry = Registry();
        var store = Store(registry);
        var output = Path.Combine(_dir, "smooth.txt");
        Assert.True(store.Update(JsonNode.Parse($$"""
            { "leds": [{ "hmin": 0, "hmax": 1, "vmin": 0, "vmax": 1 }],
              "device": { "type": "file", "output": {{JsonValue.Create(output).ToJsonString()}}, "printTimeStamp": false },
              "smoothing": { "enable": true, "type": "RgbInterpolator", "time_ms": 400, "updateFrequency": 50, "smoothingFactor": 0, "stiffness": 150, "damping": 26, "y_limit": 0, "antiFlickeringFilter": false, "continuousOutput": false } }
            """)!.AsObject()).Success);
        using (var engine = new LightingEngine(store, new SolidSource(0, 0, 0), Devices(registry), new SystemClock(), NullLoggerFactory.Instance))
        {
            Assert.True(engine.Components.IsOn(Component.Smoothing));
            engine.Start();
            WaitUntil(() => FirstDevice(engine)?.State == DeviceState.Ready);

            // a smoothing restart forgets the colors: the static color is drawn again, so the next step still fades
            engine.SetColor(1, new Rgb8(0, 0, 0), 0, "test");
            Thread.Sleep(300);
            var smoothing = store.GetSection("smoothing")!.AsObject();
            smoothing["type"] = "Stepper";
            Assert.True(store.Update(new JsonObject { ["smoothing"] = smoothing.DeepClone() }).Success);
            Thread.Sleep(300);
            engine.SetColor(1, new Rgb8(255, 255, 255), 0, "test");
            Assert.Equal("COLOR", engine.Priorities[0].ComponentId);
            var preview = new byte[3];
            WaitUntil(() => engine.LedColors.CopyLatest(preview) == 3 && preview[0] == 255);
            Assert.InRange(engine.SmoothingTicks, 10, 40);
        }

        // the controller got the transition, not a jump
        var reds = File.ReadAllLines(output).Select(l => l.Trim(' ', '[', ']', '{', '}').Split(',')[0]).Where(v => v.Length > 0).Select(int.Parse).ToList();
        Assert.Contains(reds, r => r is > 20 and < 235);
    }

    [Fact]
    public void Engine_ComponentsSwitchTheSourceAndTheDevices()
    {
        var registry = Registry();
        var store = Store(registry);
        using var engine = new LightingEngine(store, new SolidSource(255, 255, 255), Devices(registry), new SystemClock(), NullLoggerFactory.Instance);
        engine.Start();

        // the screen registers inactive and becomes active with its first frame
        WaitUntil(() => FirstDevice(engine)?.State == DeviceState.Ready && engine.Priorities is [{ Active: true, Visible: true }]);
        var prioritiesChanged = 0;
        engine.PrioritiesChanged += () => prioritiesChanged++;
        Assert.Equal("SYSTEMGRABBER", Assert.Single(engine.Priorities).ComponentId);

        engine.Components.Set(Component.All, false);

        WaitUntil(() => engine.Devices[0].State == DeviceState.Off);
        Assert.Empty(engine.Priorities);
        Assert.Equal(1, prioritiesChanged);
        var preview = new byte[3];
        WaitUntil(() => engine.LedColors.CopyLatest(preview) == 3 && preview.All(b => b == 0));

        engine.Components.Set(Component.All, true);
        WaitUntil(() => engine.Devices[0].State == DeviceState.Ready);
    }
}
