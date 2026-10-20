using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Overglow.Devices;
using Overglow.Engine;
using Overglow.Engine.Platform;

namespace Overglow.Server.Diagnostics;

/// <summary>
/// Builds <c>performance-update</c> reports in HyperHDR's PerformanceReport format: frame loop rate (type 2), one LED
/// report per controller (type 3: update rate, totals, drops), CPU (type 4) and memory (type 5). CPU and memory are the
/// whole computer's, as in HyperHDR (SystemPerformanceCounters), from the platform's <see cref="ISystemMetrics"/>.
/// </summary>
/// <remarks>
/// The LED reports keep HyperHDR's fields (<c>id</c> = the controller's position, <c>name</c>, <c>param1</c> updates
/// per second, <c>param2</c> written, <c>param3</c> submitted, <c>param4</c> dropped since the last report) and add
/// <c>controller</c> (its id), <c>controllerName</c>, <c>ledCount</c>, <c>state</c> and <c>latencyMs</c> (average
/// time from the pipeline handing over a frame until the driver had sent it; -1 without new frames).
/// </remarks>
public sealed class PerformanceMonitor(LightingEngine engine, ISystemMetrics metrics)
{
    private const int InstanceReport = 2;
    private const int LedReport = 3;
    private const int CpuReport = 4;
    private const int RamReport = 5;
    private const double BytesPerMegabyte = 1024.0 * 1024.0;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, FrameStatsSnapshot> _lastStats = new(StringComparer.Ordinal);
    private long _lastTicks = Stopwatch.GetTimestamp();
    private long _lastFrames = engine.FrameCount;

    /// <summary>Reports since the previous call: <c>{ broadcast: [...] }</c>.</summary>
    public JsonObject Report()
    {
        var devices = engine.Devices;
        var reports = new JsonArray();
        lock (_lock)
        {
            var now = Stopwatch.GetTimestamp();
            var seconds = Math.Max(Stopwatch.GetElapsedTime(_lastTicks, now).TotalSeconds, 0.001);
            var frames = engine.FrameCount;
            reports.Add(Report(InstanceReport, 0, "Overglow", (frames - _lastFrames) / seconds, 0, 0, 0, token: 1));
            _lastTicks = now;
            _lastFrames = frames;

            for (var i = 0; i < devices.Count; i++)
            {
                var d = devices[i];
                var stats = d.Stats;
                var last = _lastStats.TryGetValue(d.Id, out var previous) && previous.Written <= stats.Written ? previous : default;
                _lastStats[d.Id] = stats;
                var name = d.Status is null ? d.Type : $"{d.Type}  {d.Status}";
                var report = Report(LedReport, i, name, (stats.Written - last.Written) / seconds, stats.Written, stats.Submitted, stats.Dropped - last.Dropped, token: 1);
                report["controller"] = d.Id;
                report["controllerName"] = d.Name;
                report["ledCount"] = d.LedCount;
                report["state"] = d.Running ? d.State.ToString() : "Disabled";
                report["latencyMs"] = Math.Round(stats.AverageLatencyMsSince(last) ?? -1, 2);
                reports.Add(report);
            }

            foreach (var gone in _lastStats.Keys.Where(id => devices.All(d => d.Id != id)).ToList())
            {
                _lastStats.Remove(gone);
            }
        }

        var load = metrics.Sample();
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
