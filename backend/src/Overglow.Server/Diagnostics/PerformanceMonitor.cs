using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Overglow.Engine;
using Overglow.Engine.Platform;

namespace Overglow.Server.Diagnostics;

/// <summary>
/// Builds <c>performance-update</c> reports in HyperHDR's PerformanceReport format: frame loop rate (type 2), LED
/// device rate and drops (type 3), CPU (type 4) and memory (type 5). CPU and memory are the whole computer's, as in
/// HyperHDR (SystemPerformanceCounters), from the platform's <see cref="ISystemMetrics"/>.
/// </summary>
public sealed class PerformanceMonitor(LightingEngine engine, ISystemMetrics metrics)
{
    private const int InstanceReport = 2;
    private const int LedReport = 3;
    private const int CpuReport = 4;
    private const int RamReport = 5;
    private const double BytesPerMegabyte = 1024.0 * 1024.0;

    private readonly Lock _lock = new();
    private long _lastTicks = Stopwatch.GetTimestamp();
    private long _lastFrames = engine.FrameCount;
    private long _lastDropped;
    private long _lastWritten;

    /// <summary>Reports since the previous call: <c>{ broadcast: [...] }</c>.</summary>
    public JsonObject Report()
    {
        double fps, ledFps;
        long dropped;
        SystemLoad load;
        var devices = engine.Devices;
        var totalDropped = devices.Sum(d => d.Stats.Dropped);
        var totalWritten = devices.Sum(d => d.Stats.Written);
        lock (_lock)
        {
            var now = Stopwatch.GetTimestamp();
            var seconds = Math.Max(Stopwatch.GetElapsedTime(_lastTicks, now).TotalSeconds, 0.001);
            var frames = engine.FrameCount;
            fps = (frames - _lastFrames) / seconds;
            dropped = totalDropped - _lastDropped;
            ledFps = (totalWritten - _lastWritten) / seconds;
            load = metrics.Sample();
            _lastTicks = now;
            _lastWritten = totalWritten;
            _lastFrames = frames;
            _lastDropped = totalDropped;
        }

        var reports = new JsonArray { Report(InstanceReport, 0, "Overglow", fps, 0, 0, 0, token: 1) };
        for (var i = 0; i < devices.Count; i++)
        {
            var d = devices[i];
            var name = d.Status is null ? d.Type : $"{d.Type}  {d.Status}";
            // Rates are tracked for the controllers together; with one controller that is its own rate.
            reports.Add(Report(LedReport, i, name, i == 0 ? ledFps : -1, d.Stats.Written, d.Stats.Submitted, i == 0 ? dropped : 0, token: 1));
        }

        if (load.CpuPercent is { } cpu)
        {
            reports.Add(Report(CpuReport, -1, new JsonObject { ["cores"] = new JsonArray(), ["total"] = Math.Round(cpu, 1) }.ToJsonString(), 0, 0, 0, 0, token: 0));
        }

        if (load is { UsedMemoryBytes: { } used, TotalMemoryBytes: { } physical } && physical > 0)
        {
            // megabytes and a whole percentage, as HyperHDR
            var taken = (long)(used / BytesPerMegabyte);
            var total = (long)(physical / BytesPerMegabyte);
            var ram = new JsonObject { ["aspect"] = taken * 100 / Math.Max(total, 1), ["takenMem"] = taken, ["totalPhysMem"] = total };
            reports.Add(Report(RamReport, -1, ram.ToJsonString(), 0, 0, 0, 0, token: 0));
        }

        return new JsonObject { ["broadcast"] = reports };
    }

    private static JsonObject Report(int type, int id, string name, double p1, double p2, double p3, double p4, int token) => new()
    {
        ["type"] = type,
        ["id"] = id,
        ["name"] = name,
        ["param1"] = Math.Round(p1, 2),
        ["param2"] = p2,
        ["param3"] = p3,
        ["param4"] = p4,
        ["token"] = token,
        ["remove"] = 0,
        ["refresh"] = 0,
        ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
    };
}
