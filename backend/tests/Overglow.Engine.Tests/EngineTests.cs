using System.Diagnostics;
using System.Numerics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Overglow.Capture;
using Overglow.Core.Color;
using Overglow.Core.Imaging;
using Overglow.Core.Layout;
using Overglow.Core.Mapping;
using Overglow.Devices;
using Overglow.Settings;
using Overglow.Settings.Storage;

namespace Overglow.Engine.Tests;

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
        return new(database, SettingsSchema.CreateBuiltin(registry.Drivers.ToDictionary(d => d.Type, d => d.Schema)), NullLogger<SqliteSettingsStore>.Instance);
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

    [Fact]
    public void Pipeline_SteadyStateDoesNotAllocate()
    {
        var registry = Registry();
        using var devices = Devices(registry);
        devices.Configure(JsonNode.Parse("""{ "type": "file", "output": "/dev/null", "printTimeStamp": true }""")!.AsObject(), 168);
        devices.SetEnabled(true);
        var layout = LedLayout.FromJson(JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "layouts", "owner-168.json"))));
        var config = new PipelineConfig(new LedMappingOptions(layout), new ColorProcessor(new ColorAdjustment { ScaleOutput = 0.8f }));
        using var pipeline = new LightingPipeline(
            new SyntheticFrameSource(new SyntheticFrameSourceOptions(FramesPerSecond: 100_000)),
            new ComponentStates(),
            devices,
            new LedColorsFeed(),
            config,
            NullLogger<LightingPipeline>.Instance);
        Frame? frame = null;
        var colors = Array.Empty<Vector3>();
        for (var i = 0; i < 5; i++)
        {
            pipeline.RunOnce(ref frame, ref colors, TestContext.Current.CancellationToken);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 500; i++)
        {
            pipeline.RunOnce(ref frame, ref colors, TestContext.Current.CancellationToken);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(505, pipeline.FrameCount);
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

        using (var engine = new LightingEngine(store, new SolidSource(255, 0, 0), Devices(registry), NullLoggerFactory.Instance))
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
        using var engine = new LightingEngine(store, new SolidSource(10, 20, 30), Devices(registry), NullLoggerFactory.Instance);
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
    public void Engine_ComponentsSwitchTheSourceAndTheDevices()
    {
        var registry = Registry();
        var store = Store(registry);
        using var engine = new LightingEngine(store, new SolidSource(255, 255, 255), Devices(registry), NullLoggerFactory.Instance);
        engine.Start();
        var prioritiesChanged = 0;
        engine.PrioritiesChanged += () => prioritiesChanged++;
        WaitUntil(() => FirstDevice(engine)?.State == DeviceState.Ready);
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
