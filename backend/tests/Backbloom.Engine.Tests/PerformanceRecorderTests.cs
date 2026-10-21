using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Backbloom.Devices;
using Backbloom.Engine.Diagnostics;
using Backbloom.Engine.Platform;
using Backbloom.Settings;
using Backbloom.Settings.Storage;

namespace Backbloom.Engine.Tests;

/// <summary>
/// The performance samples (a fake clock for the recorder, the real engine with a fast and a slow controller). Not in
/// parallel with other tests: the engines and the slow controller would load the allocation tests' threads.
/// </summary>
[Collection(SerialTests.Name)]
public sealed class PerformanceRecorderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"backbloom-performance-{Guid.NewGuid():N}");
    private readonly List<IDisposable> _owned = [];
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 20, 19, 0, 0, TimeSpan.Zero));
    private readonly RecordingLogger _logger = new();

    public PerformanceRecorderTests()
    {
        Directory.CreateDirectory(_dir);
        _time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(2), "test", "test"));
    }

    public void Dispose()
    {
        _owned.ForEach(d => d.Dispose());
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // a device thread may still hold a file for a moment
        }
    }

    private string Layout() => $$"""
        { "version": 1,
          "room": { "width": 4, "depth": 4, "height": 2.6, "screen": { "x": 1.7, "y": 1.31, "width": 0.6, "height": 0.34 }, "furniture": "desk" },
          "controllers": [
            { "id": "desk", "name": "Desk", "outputs": [{ "index": 0, "start": 0, "length": 10 }],
              "device": { "type": "file", "output": {{JsonValue.Create(Path.Combine(_dir, "desk.txt")).ToJsonString()}}, "printTimeStamp": false } },
            { "id": "slow", "name": "Slow", "outputs": [{ "index": 0, "start": 0, "length": 10 }], "device": { "type": "slow" } },
            { "id": "meter", "name": "Meter", "outputs": [{ "index": 0, "start": 0, "length": 4 }], "device": { "type": "telemetry" } } ],
          "strips": [
            { "id": "a", "name": "A", "controllerId": "desk", "output": 0, "ledCount": 10,
              "path": [{ "surface": "front", "x": 2.3, "y": 1.65 }, { "surface": "front", "x": 1.7, "y": 1.65 }] },
            { "id": "b", "name": "B", "controllerId": "slow", "output": 0, "ledCount": 10,
              "path": [{ "surface": "left", "x": 3.5, "y": 1.7 }, { "surface": "left", "x": 0.5, "y": 1.7 }] },
            { "id": "c", "name": "C", "controllerId": "meter", "output": 0, "ledCount": 4,
              "path": [{ "surface": "right", "x": 1, "y": 1.2 }, { "surface": "right", "x": 2, "y": 1.2 }] } ] }
        """;

    private (LightingEngine Engine, PerformanceRecorder Recorder) Start()
    {
        var registry = new DriverRegistry().RegisterAll();
        registry.Register(SlowDriver.Descriptor);
        registry.Register(TelemetryDriver.Descriptor(_time));
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
            ["layout"] = JsonNode.Parse(Layout()),
        });
        Assert.True(result.Success, string.Join("; ", result.Errors));
        var devices = new DeviceManager(registry, new DriverContext(NullLoggerFactory.Instance, new HttpClient()), NullLoggerFactory.Instance);
        var engine = new LightingEngine(store, new SolidSource(0, 255, 0), devices, new SystemClock(), NullLoggerFactory.Instance);
        _owned.Insert(0, engine);
        engine.Start();
        var recorder = new PerformanceRecorder(engine, new FixedMetrics(), new FixedGpu(), _time, _logger, "1.2.3+test");
        _owned.Insert(0, recorder);
        return (engine, recorder);
    }

    private void Advance(TimeSpan span)
    {
        for (var passed = TimeSpan.Zero; passed < span; passed += PerformanceRecorder.CpuInterval)
        {
            _time.Advance(PerformanceRecorder.CpuInterval);
        }
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), "timed out");
            Thread.Sleep(10);
        }
    }

    [Fact]
    public void ASample_DescribesTheAppTheComputerAndEachController_AndTheLogSaysWhenOneCantKeepUp()
    {
        var (engine, recorder) = Start();
        var lines = new List<string>();
        recorder.Recorded += lines.Add;
        WaitUntil(() => engine.Devices.All(d => d.State == DeviceState.Ready));
        Assert.Empty(recorder.Recent());
        recorder.Start();
        long Dropped() => engine.Devices.Single(d => d.Id == "slow").Stats.Dropped;
        var droppedAtStart = Dropped();

        // first the start line
        var start = JsonNode.Parse(Assert.Single(lines))!.AsObject();
        Assert.Equal((1, "start", "2026-10-20T21:00:00.000+02:00", "1.2.3+test"), ((int)start["v"]!, (string)start["kind"]!, (string)start["time"]!, (string)start["version"]!));
        Assert.Equal((Environment.ProcessorCount, Environment.ProcessId), ((int)start["cores"]!, (int)start["pid"]!));
        Assert.False(string.IsNullOrEmpty((string?)start["os"]));
        Assert.NotNull(start["processStart"]);

        Advance(PerformanceRecorder.RecordInterval - PerformanceRecorder.CpuInterval);
        Assert.Single(lines);
        WaitUntil(() => Dropped() >= droppedAtStart + 20);
        Advance(PerformanceRecorder.CpuInterval);

        Assert.Equal(2, lines.Count);
        var line = lines[1];
        Assert.DoesNotContain('\n', line);
        Assert.Equal(lines, recorder.Recent());
        var sample = JsonNode.Parse(line)!.AsObject();
        Assert.Equal(PerformanceRecorder.FormatVersion, (int)sample["v"]!);
        Assert.Equal("sample", (string)sample["kind"]!);
        Assert.Equal("2026-10-20T21:00:10.000+02:00", (string)sample["time"]!);
        Assert.Equal(10, (double)sample["seconds"]!);

        var app = sample["app"]!;
        Assert.InRange((double)app["cpu"]!, 0, 100);
        Assert.InRange((double)app["cpuPeak"]!, 0, 100);
        Assert.True((double)app["privateMb"]! > 0);
        Assert.True((double)app["workingSetMb"]! > 0);
        Assert.True((double)app["heapMb"]! > 0);
        Assert.True((double)app["allocatedMbPerSecond"]! >= 0);
        Assert.Equal(3, app["gcCollections"]!.AsArray().Count);
        Assert.True((double)app["gcPauseMs"]! >= 0);
        Assert.True((int)app["threads"]! > 0);
        Assert.True((int)app["handles"]! > 0);

        Assert.Equal((3.6, 4.0, 64.0, 16.0), ((double)app["gpu"]!, (double)app["gpuPeak"]!, (double)app["gpuMemoryMb"]!, (double)app["gpuSharedMemoryMb"]!));
        Assert.Equal(
            """{"cpu":12.5,"memoryUsedMb":8192,"memoryTotalMb":16384,"gpu":36,"gpuPeak":40,"gpuMemoryUsedMb":3072}""",
            sample["computer"]!.ToJsonString());
        Assert.Equal(engine.SourceName, (string)sample["lighting"]!["source"]!);
        Assert.Null(sample["lighting"]!["profile"]);
        Assert.True((double)sample["lighting"]!["framesPerSecond"]! > 0);

        var controllers = sample["controllers"]!.AsArray();
        Assert.Equal(["Desk", "Slow", "Meter"], controllers.Select(c => (string)c!["name"]!));
        var desk = controllers[0]!;
        Assert.Equal(("desk", "file", "Ready", 10), ((string)desk["id"]!, (string)desk["type"]!, (string)desk["state"]!, (int)desk["leds"]!));
        Assert.True((double)desk["updatesPerSecond"]! > 0);
        Assert.Equal(0, (long)desk["writeErrors"]!);
        Assert.NotNull(desk["latencyMs"]);
        Assert.True((long)controllers[1]!["dropped"]! >= 20);

        // the load of what was sent (green on every LED: a third of full white, at most, nearly all of it green); the file
        // driver gets no current estimate
        var load = (double)desk["load"]!;
        Assert.InRange(load, 1, 34);
        Assert.InRange((double)desk["loadPeak"]!, load, 34);
        Assert.InRange((double)desk["brightestLedPeak"]!, load, 34);
        var rgb = desk["loadRgb"]!.AsArray().Select(c => (double)c!).ToArray();
        Assert.InRange(rgb[0], 0, 2);
        Assert.InRange(rgb[2], 0, 2);
        Assert.Equal(load * 3, rgb[0] + rgb[1] + rgb[2], 0.2);
        Assert.True((double)desk["writeMs"]! >= 0);
        Assert.Null(desk["estimatedMa"]);
        Assert.Null(desk["controller"]);

        // a strip controller: the current at the LED current it reports (30 mA)
        var meter = controllers[2]!;
        Assert.Equal(30, (double)meter["milliampsPerLed"]!);
        Assert.Equal(Math.Round((double)meter["load"]! / 100 * 4 * 30), (double)meter["estimatedMa"]!, 0.6);

        // what a controller reports about itself
        Assert.Equal(
            """{"ageSeconds":0,"powerMa":1950,"powerLimitMa":2000,"milliampsPerLed":30,"brightness":200,"forceMaxBrightness":false,"on":true,"framesPerSecond":58,"leds":4,"live":true,"liveMode":"UDP","liveSource":"192.0.2.1","realtimeOverride":0,"clients":1,"signal":75,"rssi":-62,"wifiChannel":6,"freeMemoryKb":100,"uptimeSeconds":7200,"restarts":1,"temperatureC":70.5,"temperatureSource":"Temperature","temperatureIsChip":false,"chip":"esp32","firmware":"0.15.0"}""",
            meter["controller"]!.ToJsonString());
        Assert.Contains(_logger.Messages, m => m == "Warning: Controller Meter: its power limiter holds the LEDs at the limit (1950 of 2000 mA): they would draw more");
        Assert.Contains(_logger.Messages, m => m == "Warning: Controller Meter: Temperature reads 70.5 °C");

        Assert.Contains(_logger.Messages, m => m.StartsWith("Warning: Controller Slow dropped ", StringComparison.Ordinal));
    }

    [Fact]
    public void TheLast30MinutesStayInMemory_AndNothingIsRecordedOnceDisposed()
    {
        var (_, recorder) = Start();
        var lines = new List<string>();
        recorder.Recorded += lines.Add;
        recorder.Start();

        // the start line, then 185 samples: the start line and the newest 180 are kept
        Advance(PerformanceRecorder.RecordInterval * (PerformanceRecorder.RetainedSamples + 5));
        Assert.Equal(PerformanceRecorder.RetainedSamples + 6, lines.Count);
        Assert.Equal([lines[0], .. lines[6..]], recorder.Recent());
        Assert.EndsWith("T21:30:50.000+02:00", (string)JsonNode.Parse(recorder.Recent()[^1])!["time"]!, StringComparison.Ordinal);

        recorder.Dispose();
        Advance(PerformanceRecorder.RecordInterval * 2);
        Assert.Equal(PerformanceRecorder.RetainedSamples + 6, lines.Count);
    }

    // A controller that reports near its power limit and hot, as of the recorder's clock.
    private sealed class TelemetryDriver(TimeProvider time) : ITelemetryDriver
    {
        public static DriverDescriptor Descriptor(TimeProvider time) => new(
            "telemetry", "leds_group_5_debug", JsonNode.Parse("""{ "type": "object", "properties": {} }""")!.AsObject(), (_, _) => new TelemetryDriver(time));

        public string Type => "telemetry";

        public RetryPolicy RetryPolicy => RetryPolicy.None;

        public string? Status => null;

        public ControllerTelemetry? Telemetry => new(time.GetUtcNow())
        {
            PowerMilliamps = 1950, PowerLimitMilliamps = 2000, MilliampsPerLed = 30, Brightness = 200, ForceMaxBrightness = false, On = true,
            FramesPerSecond = 58, LedCount = 4, Live = true, LiveMode = "UDP", LiveSource = "192.0.2.1", RealtimeOverride = 0, Clients = 1,
            SignalPercent = 75, Rssi = -62, WifiChannel = 6, FreeMemoryBytes = 102400, UptimeSeconds = 7200, Restarts = 1,
            TemperatureCelsius = 70.5, TemperatureSource = "Temperature", Chip = "esp32", Firmware = "0.15.0",
        };

        public ValueTask OpenAsync(int ledCount, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public bool Write(ReadOnlySpan<byte> colors) => true;

        public ValueTask CloseAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public void Dispose()
        {
        }
    }

    // 20 % for the computer and 2 % for the app on the first tick, then 40 % and 4 % (the average is weighted by time)
    private sealed class FixedGpu : IGpuMetrics
    {
        private int _samples;

        public GpuLoad Sample() => ++_samples == 1
            ? new GpuLoad(20, 2, 3L << 30, 64L << 20, 16L << 20)
            : new GpuLoad(40, 4, 3L << 30, 64L << 20, 16L << 20);
    }

    private sealed class FixedMetrics : ISystemMetrics
    {
        public SystemLoad Sample() => new(12.5, 8L << 30, 16L << 30);
    }

    private sealed class RecordingLogger : ILogger<PerformanceRecorder>
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _messages.Enqueue($"{logLevel}: {formatter(state, exception)}");
    }
}

/// <summary>Tests that run on their own, after the parallel ones.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialTests
{
    public const string Name = "Serial";
}
