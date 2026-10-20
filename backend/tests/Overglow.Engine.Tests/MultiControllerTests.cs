using System.Diagnostics;
using System.Numerics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Overglow.Devices;
using Overglow.Engine.Platform;
using Overglow.Settings;
using Overglow.Settings.Storage;

namespace Overglow.Engine.Tests;

/// <summary>A driver whose writes take 300 ms (a controller on a bad Wi-Fi link).</summary>
internal sealed class SlowDriver : ILedDriver
{
    public const string TypeName = "slow";

    public static DriverDescriptor Descriptor { get; } = new(
        TypeName, "leds_group_5_debug", JsonNode.Parse("""{ "type": "object", "properties": {} }""")!.AsObject(), (_, _) => new SlowDriver());

    public string Type => TypeName;

    public RetryPolicy RetryPolicy => RetryPolicy.None;

    public string? Status => null;

    public ValueTask OpenAsync(int ledCount, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public bool Write(ReadOnlySpan<byte> colors)
    {
        Thread.Sleep(300);
        return true;
    }

    public ValueTask CloseAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public void Dispose()
    {
    }
}

public sealed class MultiControllerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"overglow-multi-{Guid.NewGuid():N}");
    private readonly List<SettingsDatabase> _databases = [];

    public MultiControllerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _databases.ForEach(d => d.Dispose());
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // a device thread may still hold a file for a moment
        }
    }

    private static DriverRegistry Registry()
    {
        var registry = new DriverRegistry().RegisterAll();
        registry.Register(SlowDriver.Descriptor);
        return registry;
    }

    private static DeviceManager Devices(DriverRegistry registry) =>
        new(registry, new DriverContext(NullLoggerFactory.Instance, new HttpClient()), NullLoggerFactory.Instance);

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

    private string File(string name) => JsonValue.Create(Path.Combine(_dir, name)).ToJsonString();

    private SqliteSettingsStore Store(DriverRegistry registry)
    {
        var database = SettingsDatabase.Open(Path.Combine(_dir, SettingsDatabase.FileName));
        _databases.Add(database);
        var store = new SqliteSettingsStore(database, SettingsSchema.CreateBuiltin(registry.Drivers.ToDictionary(d => d.Type, d => d.Schema)), NullLogger<SqliteSettingsStore>.Instance);
        var foreground = store.GetSection("foregroundEffect")!.AsObject();
        foreground["enable"] = false;
        var smoothing = store.GetSection("smoothing")!.AsObject();
        smoothing["enable"] = false;

        // without smoothing every captured frame goes out at once
        Assert.True(store.Update(new JsonObject { ["foregroundEffect"] = foreground.DeepClone(), ["smoothing"] = smoothing.DeepClone() }).Success);
        return store;
    }

    // Controller "tv": the owner's split, 91 + 77 LEDs; "desk": 30 LEDs at half brightness; "shelf": 4 lit + 2 dark LEDs.
    private string Layout() => $$"""
        { "version": 1,
          "controllers": [
            { "id": "tv", "name": "TV", "outputs": [{ "index": 0, "start": 0, "length": 91 }, { "index": 1, "start": 91, "length": 77 }],
              "device": { "type": "file", "output": {{File("tv.txt")}}, "printTimeStamp": false } },
            { "id": "desk", "name": "Desk", "outputs": [{ "index": 0, "start": 0, "length": 30 }],
              "device": { "type": "file", "output": {{File("desk.txt")}}, "printTimeStamp": false } },
            { "id": "shelf", "name": "Shelf", "outputs": [{ "index": 0, "start": 0, "length": 6 }],
              "device": { "type": "file", "output": {{File("shelf.txt")}}, "printTimeStamp": false } } ],
          "strips": [
            { "id": "monitor", "name": "Monitor", "controllerId": "tv", "output": 0,
              "segments": [{ "kind": "edge", "count": 91, "edge": "bottom", "from": 1, "to": 0, "depth": 0.2 }] },
            { "id": "ceiling", "name": "Ceiling", "controllerId": "tv", "output": 1,
              "segments": [{ "kind": "edge", "count": 77, "edge": "top", "from": 1, "to": 0, "depth": 0.25, "blend": 5 }] },
            { "id": "desk-strip", "name": "Desk strip", "controllerId": "desk", "output": 0, "brightness": 0.5,
              "segments": [{ "kind": "edge", "count": 30, "edge": "left", "from": 0, "to": 1, "depth": 0.1 }] },
            { "id": "shelf-strip", "name": "Shelf strip", "controllerId": "shelf", "output": 0,
              "segments": [{ "kind": "fixed", "count": 4, "area": { "hmin": 0, "hmax": 1, "vmin": 0, "vmax": 1 } }, { "kind": "off", "count": 2 }] } ] }
        """;

    // " [{r,g,b}{r,g,b}...]" -> the LEDs of one line
    private static List<(int R, int G, int B)> Leds(string line) =>
        [.. line.Trim(' ', '[', ']').Split('}', StringSplitOptions.RemoveEmptyEntries).Select(e => e.TrimStart('{').Split(',').Select(int.Parse).ToArray()).Select(v => (v[0], v[1], v[2]))];

    [Fact]
    public void OneLayout_FeedsEachControllerItsOwnSlice_OnOneClock()
    {
        var registry = Registry();
        var store = Store(registry);
        Assert.True(store.Update(new JsonObject { ["layout"] = JsonNode.Parse(Layout()) }).Success);

        using (var engine = new LightingEngine(store, new SolidSource(255, 255, 255), Devices(registry), new SystemClock(), NullLoggerFactory.Instance))
        {
            Assert.Equal(204, engine.Layout.Count);
            engine.Start();
            WaitUntil(() => engine.Devices.All(d => d.Stats.Written > 20));
            Assert.Equal([0, 168, 198], engine.Devices.Select(d => d.Start));

            // every controller got every frame: the same number of updates, give or take the ones in flight
            var written = engine.Devices.Select(d => d.Stats.Written).ToList();
            Assert.InRange(written.Max() - written.Min(), 0, 3);

            // the preview covers all LEDs, dimmed like the strips
            var preview = new byte[204 * 3];
            Assert.Equal(204 * 3, engine.LedColors.CopyLatest(preview));
            Assert.Equal(255, preview[0]);
            Assert.InRange((int)preview[(168 * 3) + 1], 127, 128);
            Assert.InRange((int)preview[(203 * 3) + 2], 0, 1);

            Assert.Equal(("desk", 3), engine.IdentifyLed("desk", 3));
            Assert.Equal(("desk", 2), engine.IdentifyLed(null, 170));
            Assert.Null(engine.IdentifyLed("shelf", 6));
            Assert.Null(engine.IdentifyLed("nowhere", 0));
        }

        var tv = System.IO.File.ReadAllLines(Path.Combine(_dir, "tv.txt")).Select(Leds).Where(l => l.Count > 0).ToList();
        var desk = System.IO.File.ReadAllLines(Path.Combine(_dir, "desk.txt")).Select(Leds).Where(l => l.Count > 0).ToList();
        var shelf = System.IO.File.ReadAllLines(Path.Combine(_dir, "shelf.txt")).Select(Leds).Where(l => l.Count > 0).ToList();
        Assert.All(tv, l => Assert.Equal(168, l.Count));
        Assert.All(desk, l => Assert.Equal(30, l.Count));
        Assert.All(shelf, l => Assert.Equal(6, l.Count));
        Assert.Contains(tv, l => l.All(c => c == (255, 255, 255)));
        Assert.Contains(desk, l => l.All(c => c.R is 127 or 128 && c.R == c.G && c.G == c.B));
        Assert.Contains(shelf, l => l.Take(4).All(c => c == (255, 255, 255)) && l.Skip(4).All(c => c.R <= 1));

        // switched off at the end: a black last frame on every controller
        Assert.All([tv[^1], desk[^1], shelf[^1]], l => Assert.All(l, c => Assert.Equal((0, 0, 0), c)));
    }

    [Fact]
    public void ASlowController_NeverDelaysTheOthersOrTheFrameLoop()
    {
        var registry = Registry();
        using var devices = Devices(registry);
        devices.Configure(
        [
            new ControllerDevice("a", "A", JsonNode.Parse("""{ "type": "file", "output": "/dev/null", "printTimeStamp": false }""")!.AsObject(), 10),
            new ControllerDevice("slow", "Slow", JsonNode.Parse("""{ "type": "slow" }""")!.AsObject(), 10),
            new ControllerDevice("c", "C", JsonNode.Parse("""{ "type": "file", "output": "/dev/null", "printTimeStamp": false }""")!.AsObject(), 10),
        ]);
        devices.SetEnabled(true);
        WaitUntil(() => devices.Devices.All(d => d.State == DeviceState.Ready));
        var colors = new Vector3[30];
        var longest = TimeSpan.Zero;
        for (var i = 0; i < 100; i++)
        {
            var sw = Stopwatch.StartNew();
            devices.Submit(colors);
            longest = sw.Elapsed > longest ? sw.Elapsed : longest;
            Thread.Sleep(10);
        }

        WaitUntil(() => devices.Devices[0].Stats.Written >= 95 && devices.Devices[2].Stats.Written >= 95);
        Assert.True(longest < TimeSpan.FromMilliseconds(50), $"Submit took {longest.TotalMilliseconds} ms");
        var slow = devices.Devices[1].Stats;
        Assert.InRange(slow.Written, 1, 10);
        Assert.True(slow.Dropped > 80);
        Assert.True(devices.Devices[0].Stats.AverageLatencyMsSince(default) < 50);
    }

    [Fact]
    public void Reconfiguring_KeepsTheControllersThatDidNotChange()
    {
        var registry = Registry();
        using var devices = Devices(registry);
        var a = new ControllerDevice("a", "A", JsonNode.Parse("""{ "type": "file", "output": "/dev/null", "printTimeStamp": false }""")!.AsObject(), 10);
        var b = new ControllerDevice("b", "B", JsonNode.Parse("""{ "type": "file", "output": "/dev/null", "printTimeStamp": false }""")!.AsObject(), 5);
        devices.Configure([a, b]);
        devices.SetEnabled(true);
        WaitUntil(() => devices.Devices.All(d => d.State == DeviceState.Ready));
        devices.Submit(new Vector3[15]);
        WaitUntil(() => devices.Devices[1].Stats.Written > 0);

        devices.Configure([b, a with { LedCount = 11 }, new ControllerDevice("off", "Off", null, 3, Enabled: false)]);

        var list = devices.Devices;
        Assert.Equal(["b", "a", "off"], list.Select(d => d.Id));
        Assert.Equal([0, 5, 16], list.Select(d => d.Start));
        Assert.True(list[0].Stats.Written > 0);
        Assert.Equal(0, list[1].Stats.Written);
        Assert.Equal((false, "Disabled"), (list[2].Running, list[2].Status));
        Assert.Equal(19, devices.LedCount);
    }
}
