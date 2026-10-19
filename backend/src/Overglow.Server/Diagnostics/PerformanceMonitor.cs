using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Overglow.Engine;

namespace Overglow.Server.Diagnostics;

/// <summary>
/// Builds <c>performance-update</c> reports in HyperHDR's PerformanceReport format: frame loop rate (type 2), LED
/// device rate and drops (type 3), CPU (type 4) and memory (type 5). CPU and memory are this process's share of the
/// machine (HyperHDR reported machine-wide load, which needs platform code that comes with the platform projects).
/// </summary>
public sealed class PerformanceMonitor(LightingEngine engine)
{
    private const int InstanceReport = 2;
    private const int LedReport = 3;
    private const int CpuReport = 4;
    private const int RamReport = 5;
    private const double BytesPerMegabyte = 1024.0 * 1024.0;

    private readonly Lock _lock = new();
    private long _lastTicks = Stopwatch.GetTimestamp();
    private long _lastFrames = engine.FrameCount;
    private TimeSpan _lastCpu = Process.GetCurrentProcess().TotalProcessorTime;
    private long _lastDropped;
    private long _lastWritten;

    /// <summary>Reports since the previous call: <c>{ broadcast: [...] }</c>.</summary>
    public JsonObject Report()
    {
        double seconds, fps, cpuPercent, ledFps;
        long dropped, workingSet;
        var devices = engine.Devices;
        var totalDropped = devices.Sum(d => d.Stats.Dropped);
        var totalWritten = devices.Sum(d => d.Stats.Written);
        lock (_lock)
        {
            var now = Stopwatch.GetTimestamp();
            seconds = Math.Max(Stopwatch.GetElapsedTime(_lastTicks, now).TotalSeconds, 0.001);
            var frames = engine.FrameCount;
            fps = (frames - _lastFrames) / seconds;
            using var process = Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            workingSet = process.WorkingSet64;
            cpuPercent = (cpu - _lastCpu).TotalSeconds / seconds / Environment.ProcessorCount * 100;
            dropped = totalDropped - _lastDropped;
            ledFps = (totalWritten - _lastWritten) / seconds;
            _lastTicks = now;
            _lastWritten = totalWritten;
            _lastFrames = frames;
            _lastCpu = cpu;
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

        reports.Add(Report(CpuReport, -1, new JsonObject { ["cores"] = new JsonArray(), ["total"] = Math.Round(cpuPercent, 1) }.ToJsonString(), 0, 0, 0, 0, token: 0));
        var total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / BytesPerMegabyte;
        var taken = workingSet / BytesPerMegabyte;
        var ram = new JsonObject
        {
            ["aspect"] = total > 0 ? (int)Math.Round(100 * taken / total) : 0,
            ["takenMem"] = (long)taken,
            ["totalPhysMem"] = (long)total,
        };
        reports.Add(Report(RamReport, -1, ram.ToJsonString(), 0, 0, 0, 0, token: 0));
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
