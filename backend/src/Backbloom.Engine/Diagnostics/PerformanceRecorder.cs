using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Backbloom.Devices;
using Backbloom.Engine.Platform;

namespace Backbloom.Engine.Diagnostics;

/// <summary>
/// Records how the app performs, for looking into a problem after the fact: a start line (<c>kind</c> "start": version,
/// operating system, cores, process id and start time), then every <see cref="RecordInterval"/> one sample (<c>kind</c>
/// "sample") with this process's CPU, GPU, memory, garbage collections, threads and handles, the computer's CPU, GPU and
/// memory, the frame loop's rate and each controller's updates, drops, write errors, latency, LED load and what the
/// controller reports about itself; JSON lines of format version <see cref="FormatVersion"/> (see backend/README.md).
/// The CPU and GPU are also measured every <see cref="CpuInterval"/> for the peak. The start line and the last <see cref="RetainedSamples"/> samples stay in memory for a crash report
/// (<see cref="Recent"/>); <see cref="Recorded"/> hands each line to the file.
/// </summary>
/// <remarks>
/// Runs on a timer thread, never on the frame loop: it reads counters the engine, the runtime and the system already
/// keep, and a failure only skips a sample (it is logged once). The log gets a warning when a controller starts
/// dropping frames (and a line when it keeps up again) and when garbage collections paused the app for
/// <see cref="LongGcPause"/> or more within one sample.
/// </remarks>
public sealed partial class PerformanceRecorder : IDisposable
{
    public const int FormatVersion = 1;
    public static readonly TimeSpan RecordInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan CpuInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan LongGcPause = TimeSpan.FromMilliseconds(100);

    /// <summary>30 minutes of samples.</summary>
    public const int RetainedSamples = 180;

    // a controller drops frames when at least this share of the frames handed to it in one sample were replaced, and
    // keeps up again below the lower share (so one around the threshold doesn't fill the log)
    private const double DroppingShare = 0.05;
    private const double KeepingUpShare = 0.01;
    private const long DroppingMinimum = 10;

    // WLED's default current of one WS281x LED at full white (its "LED current" setting): the estimate's assumption
    public const int EstimatedMilliampsPerLed = 55;

    // the controller's power limiter holds the LEDs at its limit from this share of it, and no longer below the lower one
    private const double PowerLimitShare = 0.95;
    private const double PowerLimitClearShare = 0.8;

    // a temperature sensor at the strip or the power supply reads hot from this, the controller chip's own from the
    // higher value (ESP32 chips run at 50-70 °C; WLED's own alarm is at 95 °C); fine again 5 °C below
    public const double HotCelsius = 65;
    public const double HotChipCelsius = 85;
    private const double CoolingMargin = 5;

    // drivers of lamps and the file driver: the LED current estimate means nothing for them
    private static readonly HashSet<string> s_notStrips = new(StringComparer.Ordinal)
    {
        "file", "philipshue", "nanoleaf", "lifx", "wiz", "yeelight", "home_assistant", "zigbee2mqtt",
    };
    private const double BytesPerMegabyte = 1024.0 * 1024.0;

    // for a file, never HTML: the version and the names stay readable ("+" and accented letters unescaped)
    private static readonly JsonWriterOptions s_jsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly LightingEngine _engine;
    private readonly ISystemMetrics _metrics;
    private readonly IGpuMetrics _gpuMetrics;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly string _version;
    private readonly Lock _lock = new();
    private readonly string[] _recent = new string[RetainedSamples];
    private readonly ArrayBufferWriter<byte> _buffer = new(4096);
    private readonly Dictionary<string, FrameStatsSnapshot> _lastStats = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dropping = new(StringComparer.Ordinal);
    private readonly HashSet<string> _atPowerLimit = new(StringComparer.Ordinal);
    private readonly HashSet<string> _hot = new(StringComparer.Ordinal);
    private string? _start;
    private int _recentCount;
    private int _recentNext;
    private ITimer? _timer;
    private bool _disposed;
    private bool _failed;

    // since the previous sample
    private long _sampleStart;
    private long _lastFrames;
    private long _lastSmoothingTicks;
    private int[] _lastCollections = new int[3];
    private TimeSpan _lastGcPause;
    private long _lastAllocated;
    private TimeSpan _sampleCpu;
    private double _cpuPeak;
    private GpuAverage _appGpu;
    private GpuAverage _computerGpu;
    private GpuLoad _gpu;
    private bool _gpuFailed;

    // since the previous CPU measurement
    private long _cpuStart;
    private TimeSpan _lastCpu;

    /// <param name="gpu">GPU load, sampled with the CPU every <see cref="CpuInterval"/> (null: none).</param>
    /// <param name="version">The app's version, for the start line.</param>
    public PerformanceRecorder(LightingEngine engine, ISystemMetrics metrics, IGpuMetrics? gpu, TimeProvider time, ILogger<PerformanceRecorder> logger, string version)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(version);
        _engine = engine;
        _metrics = metrics;
        _gpuMetrics = gpu ?? new NoGpuMetrics();
        _time = time;
        _logger = logger;
        _version = version;
    }

    /// <summary>Raised with the start line and each sample (one JSON line, without the line break).</summary>
    public event Action<string>? Recorded;

    /// <summary>Writes the start line and starts measuring; the first sample follows after <see cref="RecordInterval"/>.</summary>
    public void Start()
    {
        string start;
        lock (_lock)
        {
            if (_timer is not null || _disposed)
            {
                return;
            }

            start = _start = StartLine();

            var now = _time.GetTimestamp();
            var cpu = Environment.CpuUsage.TotalTime;
            _sampleStart = _cpuStart = now;
            _sampleCpu = _lastCpu = cpu;
            _lastFrames = _engine.FrameCount;
            _lastSmoothingTicks = _engine.SmoothingTicks;
            _lastCollections = Collections();
            _lastGcPause = GC.GetTotalPauseDuration();
            _lastAllocated = GC.GetTotalAllocatedBytes();
            foreach (var device in _engine.Devices)
            {
                _lastStats[device.Id] = device.Stats;
                _engine.TakePeaks(device.Id);
            }

            // the GPU counters open on the first tick (that takes ~140 ms: not on the app's start)
            _ = _metrics.Sample();
            _timer = _time.CreateTimer(_ => Tick(), null, CpuInterval, CpuInterval);
        }

        Recorded?.Invoke(start);
    }

    /// <summary>The start line and the samples of the last 30 minutes, oldest first (empty before <see cref="Start"/>).</summary>
    public IReadOnlyList<string> Recent()
    {
        lock (_lock)
        {
            if (_start is null)
            {
                return [];
            }

            var lines = new string[_recentCount + 1];
            lines[0] = _start;
            for (var i = 0; i < _recentCount; i++)
            {
                lines[i + 1] = _recent[(_recentNext - _recentCount + i + RetainedSamples) % RetainedSamples];
            }

            return lines;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }

    // Measures the CPU for the peak; records a sample on the tick closest to RecordInterval (timer ticks come a few
    // milliseconds early or late).
    private void Tick()
    {
        try
        {
            string sample;
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                var now = _time.GetTimestamp();
                var cpu = Environment.CpuUsage.TotalTime;
                var tick = _time.GetElapsedTime(_cpuStart, now);
                _cpuPeak = Math.Max(_cpuPeak, CpuPercent(cpu - _lastCpu, tick));
                _cpuStart = now;
                _lastCpu = cpu;

                // the GPU utilization is the average since the previous tick, like the CPU
                SampleGpu(tick);

                var elapsed = _time.GetElapsedTime(_sampleStart, now);
                if (elapsed < RecordInterval - (CpuInterval / 2))
                {
                    return;
                }

                sample = Record(now, elapsed, cpu);
                _recent[_recentNext] = sample;
                _recentNext = (_recentNext + 1) % RetainedSamples;
                _recentCount = Math.Min(_recentCount + 1, RetainedSamples);
            }

            Recorded?.Invoke(sample);
            _failed = false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // a diagnostics feature must not end the app it watches
            if (!_failed)
            {
                LogSampleFailed(ex);
            }

            _failed = true;
        }
    }

    // Runs under _lock. A failure leaves the GPU unknown and the rest of the sample intact (logged once).
    private void SampleGpu(TimeSpan tick)
    {
        try
        {
            _gpu = _gpuMetrics.Sample();
            _appGpu.Add(_gpu.AppPercent, tick);
            _computerGpu.Add(_gpu.ComputerPercent, tick);
            _gpuFailed = false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _gpu = default;
            if (!_gpuFailed)
            {
                LogGpuFailed(ex);
            }

            _gpuFailed = true;
        }
    }

    // Runs under _lock.
    private string StartLine()
    {
        _buffer.ResetWrittenCount();
        using (var json = new Utf8JsonWriter(_buffer, s_jsonOptions))
        using (var process = Process.GetCurrentProcess())
        {
            json.WriteStartObject();
            json.WriteNumber("v", FormatVersion);
            json.WriteString("kind", "start");
            json.WriteString("time", Timestamp(_time.GetLocalNow()));
            json.WriteString("version", _version);
            json.WriteString("os", RuntimeInformation.OSDescription);
            json.WriteString("runtime", RuntimeInformation.FrameworkDescription);
            json.WriteNumber("cores", Environment.ProcessorCount);
            json.WriteNumber("pid", Environment.ProcessId);
            json.WriteString("processStart", Timestamp(new DateTimeOffset(process.StartTime)));
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(_buffer.WrittenSpan);
    }

    private static string Timestamp(DateTimeOffset time) => time.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture);

    // Runs under _lock.
    private string Record(long now, TimeSpan elapsed, TimeSpan cpu)
    {
        var seconds = Math.Max(elapsed.TotalSeconds, 0.001);
        var frames = _engine.FrameCount;
        var smoothingTicks = _engine.SmoothingTicks;
        var collections = Collections();
        var gcPause = GC.GetTotalPauseDuration();
        var allocated = GC.GetTotalAllocatedBytes();
        var machine = _metrics.Sample();

        _buffer.ResetWrittenCount();
        using (var json = new Utf8JsonWriter(_buffer, s_jsonOptions))
        {
            json.WriteStartObject();
            json.WriteNumber("v", FormatVersion);
            json.WriteString("kind", "sample");
            json.WriteString("time", Timestamp(_time.GetLocalNow()));
            json.WriteNumber("seconds", Math.Round(seconds, 2));

            json.WriteStartObject("app");
            json.WriteNumber("cpu", Math.Round(CpuPercent(cpu - _sampleCpu, elapsed), 2));
            json.WriteNumber("cpuPeak", Math.Round(_cpuPeak, 2));
            using (var process = Process.GetCurrentProcess())
            {
                json.WriteNumber("privateMb", Megabytes(process.PrivateMemorySize64));
                json.WriteNumber("workingSetMb", Megabytes(process.WorkingSet64));
                json.WriteNumber("heapMb", Megabytes(GC.GetTotalMemory(forceFullCollection: false)));
                json.WriteNumber("allocatedMbPerSecond", Math.Round((allocated - _lastAllocated) / BytesPerMegabyte / seconds, 3));
                json.WriteStartArray("gcCollections");
                for (var generation = 0; generation < collections.Length; generation++)
                {
                    json.WriteNumberValue(collections[generation] - _lastCollections[generation]);
                }

                json.WriteEndArray();
                json.WriteNumber("gcPauseMs", Math.Round((gcPause - _lastGcPause).TotalMilliseconds, 2));
                json.WriteNumber("threads", process.Threads.Count);
                json.WriteNumber("handles", process.HandleCount);
            }

            WriteNumberOrNull(json, "gpu", _appGpu.Average);
            WriteNumberOrNull(json, "gpuPeak", _appGpu.Peak);
            WriteNumberOrNull(json, "gpuMemoryMb", _gpu.AppDedicatedBytes is { } dedicated ? Megabytes(dedicated) : null);
            WriteNumberOrNull(json, "gpuSharedMemoryMb", _gpu.AppSharedBytes is { } shared ? Megabytes(shared) : null);
            json.WriteEndObject();

            json.WriteStartObject("computer");
            WriteNumberOrNull(json, "cpu", machine.CpuPercent is { } machineCpu ? Math.Round(machineCpu, 1) : null);
            WriteNumberOrNull(json, "memoryUsedMb", machine.UsedMemoryBytes is { } used ? Math.Round(used / BytesPerMegabyte) : null);
            WriteNumberOrNull(json, "memoryTotalMb", machine.TotalMemoryBytes is { } total ? Math.Round(total / BytesPerMegabyte) : null);
            WriteNumberOrNull(json, "gpu", _computerGpu.Average);
            WriteNumberOrNull(json, "gpuPeak", _computerGpu.Peak);
            WriteNumberOrNull(json, "gpuMemoryUsedMb", _gpu.ComputerDedicatedBytes is { } gpuUsed ? Math.Round(gpuUsed / BytesPerMegabyte) : null);
            json.WriteEndObject();

            json.WriteStartObject("lighting");
            json.WriteString("source", _engine.SourceName);
            json.WriteString("profile", _engine.ProfileId);
            json.WriteNumber("framesPerSecond", Math.Round((frames - _lastFrames) / seconds, 2));
            json.WriteNumber("smoothingPerSecond", Math.Round((smoothingTicks - _lastSmoothingTicks) / seconds, 2));
            json.WriteEndObject();

            json.WriteStartArray("controllers");
            WriteControllers(json, seconds);
            json.WriteEndArray();

            json.WriteEndObject();
        }

        if (gcPause - _lastGcPause >= LongGcPause)
        {
            LogLongGcPause(Math.Round((gcPause - _lastGcPause).TotalMilliseconds), Math.Round(seconds));
        }

        _sampleStart = now;
        _sampleCpu = cpu;
        _cpuPeak = 0;
        _appGpu = default;
        _computerGpu = default;
        _lastFrames = frames;
        _lastSmoothingTicks = smoothingTicks;
        _lastCollections = collections;
        _lastGcPause = gcPause;
        _lastAllocated = allocated;
        return Encoding.UTF8.GetString(_buffer.WrittenSpan);
    }

    // Runs under _lock.
    private void WriteControllers(Utf8JsonWriter json, double seconds)
    {
        var devices = _engine.Devices;
        foreach (var device in devices)
        {
            var stats = device.Stats;
            var last = _lastStats.TryGetValue(device.Id, out var previous) && previous.Written <= stats.Written ? previous : default;
            _lastStats[device.Id] = stats;
            var submitted = stats.Submitted - last.Submitted;
            var dropped = stats.Dropped - last.Dropped;

            json.WriteStartObject();
            json.WriteString("id", device.Id);
            json.WriteString("name", device.Name);
            json.WriteString("type", device.Type);
            json.WriteString("state", device.Running ? device.State.ToString() : "Disabled");
            json.WriteNumber("leds", device.LedCount);
            json.WriteNumber("updatesPerSecond", Math.Round((stats.Written - last.Written) / seconds, 2));
            json.WriteNumber("submitted", submitted);
            json.WriteNumber("dropped", dropped);
            json.WriteNumber("writeErrors", stats.WriteErrors - last.WriteErrors);
            WriteNumberOrNull(json, "latencyMs", stats.AverageLatencyMsSince(last) is { } latency ? Math.Round(latency, 2) : null);

            WriteNumberOrNull(json, "writeMs", stats.AverageWriteMsSince(last) is { } write ? Math.Round(write, 3) : null);

            // what Backbloom asks the LEDs to draw, before the controller's own brightness and power limit
            var load = stats.AverageLoadSince(last);
            var peaks = _engine.TakePeaks(device.Id) ?? default;
            WriteNumberOrNull(json, "writeMsPeak", stats.WriteCalls > last.WriteCalls ? Math.Round(peaks.WriteMs, 3) : null);
            WriteNumberOrNull(json, "load", Percent(load));
            WriteNumberOrNull(json, "loadPeak", load is null ? null : Percent(peaks.Load));
            WriteNumberOrNull(json, "brightestLedPeak", load is null ? null : Percent(peaks.Led));
            if (stats.AverageChannelsSince(last) is { } channels)
            {
                json.WriteStartArray("loadRgb");
                json.WriteNumberValue(Percent(channels.X)!.Value);
                json.WriteNumberValue(Percent(channels.Y)!.Value);
                json.WriteNumberValue(Percent(channels.Z)!.Value);
                json.WriteEndArray();
            }
            else
            {
                json.WriteNull("loadRgb");
            }

            // the current that means, for LED strips (lamps and the file driver draw what they draw)
            var perLed = device.Telemetry?.MilliampsPerLed ?? EstimatedMilliampsPerLed;
            var strip = !s_notStrips.Contains(device.Type);
            WriteNumberOrNull(json, "milliampsPerLed", strip ? perLed : null);
            WriteNumberOrNull(json, "estimatedMa", strip && load is { } a ? Math.Round(a * device.LedCount * perLed) : null);
            WriteNumberOrNull(json, "estimatedPeakMa", strip && load is not null ? Math.Round(peaks.Load * device.LedCount * perLed) : null);
            WriteTelemetry(json, device);
            json.WriteEndObject();

            // only a connected controller can fall behind (drops are counted only while it is ready, too)
            var dropping = device.State == DeviceState.Ready && dropped >= DroppingMinimum && dropped >= submitted * DroppingShare;
            if (dropping && _dropping.Add(device.Id))
            {
                LogDropping(device.Name, dropped, submitted, Math.Round(seconds));
            }
            else if (submitted > 0 && dropped < submitted * KeepingUpShare && _dropping.Remove(device.Id))
            {
                LogKeepsUp(device.Name);
            }
        }

        foreach (var gone in _lastStats.Keys.Where(id => devices.All(d => d.Id != id)).ToList())
        {
            _lastStats.Remove(gone);
            _dropping.Remove(gone);
            _atPowerLimit.Remove(gone);
            _hot.Remove(gone);
        }
    }

    // What the controller reports about itself ("controller": null when its driver doesn't read that). Runs under _lock.
    private void WriteTelemetry(Utf8JsonWriter json, DeviceInfo device)
    {
        if (device.Telemetry is not { } t)
        {
            // switched off or not reported: nothing to judge until it reports again
            json.WriteNull("controller");
            _atPowerLimit.Remove(device.Id);
            _hot.Remove(device.Id);
            return;
        }

        json.WriteStartObject("controller");
        json.WriteNumber("ageSeconds", Math.Round(Math.Max((_time.GetUtcNow() - t.ReadAt).TotalSeconds, 0), 1));
        WriteNumberOrNull(json, "powerMa", t.PowerMilliamps);
        WriteNumberOrNull(json, "powerLimitMa", t.PowerLimitMilliamps);
        WriteNumberOrNull(json, "milliampsPerLed", t.MilliampsPerLed);
        WriteNumberOrNull(json, "brightness", t.Brightness);
        WriteBooleanOrNull(json, "forceMaxBrightness", t.ForceMaxBrightness);
        WriteBooleanOrNull(json, "on", t.On);
        WriteNumberOrNull(json, "framesPerSecond", t.FramesPerSecond);
        WriteNumberOrNull(json, "leds", t.LedCount);
        WriteBooleanOrNull(json, "live", t.Live);
        json.WriteString("liveMode", t.LiveMode);
        json.WriteString("liveSource", t.LiveSource);
        WriteNumberOrNull(json, "realtimeOverride", t.RealtimeOverride);
        WriteNumberOrNull(json, "clients", t.Clients);
        WriteNumberOrNull(json, "signal", t.SignalPercent);
        WriteNumberOrNull(json, "rssi", t.Rssi);
        WriteNumberOrNull(json, "wifiChannel", t.WifiChannel);
        WriteNumberOrNull(json, "freeMemoryKb", t.FreeMemoryBytes is { } free ? Math.Round(free / 1024.0, 1) : null);
        WriteNumberOrNull(json, "uptimeSeconds", t.UptimeSeconds);
        json.WriteNumber("restarts", t.Restarts);
        WriteNumberOrNull(json, "temperatureC", t.TemperatureCelsius);
        json.WriteString("temperatureSource", t.TemperatureSource);
        json.WriteBoolean("temperatureIsChip", t.TemperatureIsChip);
        json.WriteString("chip", t.Chip);
        json.WriteString("firmware", t.Firmware);
        json.WriteEndObject();

        if (t is { PowerMilliamps: { } power, PowerLimitMilliamps: { } limit })
        {
            if (power >= limit * PowerLimitShare && _atPowerLimit.Add(device.Id))
            {
                LogAtPowerLimit(device.Name, power, limit);
            }
            else if (power < limit * PowerLimitClearShare && _atPowerLimit.Remove(device.Id))
            {
                LogBelowPowerLimit(device.Name);
            }
        }

        if (t.TemperatureCelsius is { } celsius)
        {
            // the chip runs hotter than the strip or the power supply may
            var hot = t.TemperatureIsChip ? HotChipCelsius : HotCelsius;
            if (celsius >= hot && _hot.Add(device.Id))
            {
                LogHot(device.Name, celsius, t.TemperatureSource ?? "sensor");
            }
            else if (celsius < hot - CoolingMargin && _hot.Remove(device.Id))
            {
                LogCooled(device.Name, celsius);
            }
        }
    }

    private static double? Percent(double? share) => share is { } s ? Math.Round(s * 100, 1) : null;

    private static void WriteBooleanOrNull(Utf8JsonWriter json, string name, bool? value)
    {
        if (value is { } v)
        {
            json.WriteBoolean(name, v);
        }
        else
        {
            json.WriteNull(name);
        }
    }

    private static void WriteNumberOrNull(Utf8JsonWriter json, string name, int? value)
    {
        if (value is { } v)
        {
            json.WriteNumber(name, v);
        }
        else
        {
            json.WriteNull(name);
        }
    }

    private static void WriteNumberOrNull(Utf8JsonWriter json, string name, long? value)
    {
        if (value is { } v)
        {
            json.WriteNumber(name, v);
        }
        else
        {
            json.WriteNull(name);
        }
    }

    private static void WriteNumberOrNull(Utf8JsonWriter json, string name, double? value)
    {
        if (value is { } v)
        {
            json.WriteNumber(name, v);
        }
        else
        {
            json.WriteNull(name);
        }
    }

    // share of all cores, as Task Manager shows it
    private static double CpuPercent(TimeSpan cpu, TimeSpan elapsed) =>
        elapsed > TimeSpan.Zero ? Math.Clamp(cpu / elapsed / Environment.ProcessorCount * 100, 0, 100) : 0;

    private static double Megabytes(long bytes) => Math.Round(bytes / BytesPerMegabyte, 1);

    private static int[] Collections() => [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];

    [LoggerMessage(Level = LogLevel.Warning, Message = "Controller {Name} dropped {Dropped} of {Submitted} frames in the last {Seconds} s: it can't keep up with the frame rate")]
    private partial void LogDropping(string name, long dropped, long submitted, double seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Controller {Name} keeps up with the frame rate again")]
    private partial void LogKeepsUp(string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Controller {Name}: its power limiter holds the LEDs at the limit ({Milliamps} of {Limit} mA): they would draw more")]
    private partial void LogAtPowerLimit(string name, int milliamps, int limit);

    [LoggerMessage(Level = LogLevel.Information, Message = "Controller {Name}: below its power limit again")]
    private partial void LogBelowPowerLimit(string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Controller {Name}: {Sensor} reads {Celsius} °C")]
    private partial void LogHot(string name, double celsius, string sensor);

    [LoggerMessage(Level = LogLevel.Information, Message = "Controller {Name}: the temperature is down to {Celsius} °C")]
    private partial void LogCooled(string name, double celsius);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't read the GPU load")]
    private partial void LogGpuFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't record a performance sample")]
    private partial void LogSampleFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Garbage collections paused Backbloom for {Milliseconds} ms in the last {Seconds} s")]
    private partial void LogLongGcPause(double milliseconds, double seconds);
}

/// <summary>A time-weighted average and the peak of the GPU utilization over the ticks of one sample.</summary>
internal struct GpuAverage
{
    private double _weighted;
    private double _seconds;
    private double? _peak;

    public readonly double? Average => _seconds > 0 ? Math.Round(_weighted / _seconds, 1) : null;

    public readonly double? Peak => _peak is { } p ? Math.Round(p, 1) : null;

    /// <summary>Adds the utilization of one tick (null: unknown, e.g. the first reading).</summary>
    public void Add(double? percent, TimeSpan tick)
    {
        if (percent is { } p && tick > TimeSpan.Zero)
        {
            _weighted += p * tick.TotalSeconds;
            _seconds += tick.TotalSeconds;
            _peak = Math.Max(_peak ?? 0, p);
        }
    }
}
