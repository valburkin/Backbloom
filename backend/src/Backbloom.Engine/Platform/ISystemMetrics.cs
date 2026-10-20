using System.Diagnostics;

namespace Backbloom.Engine.Platform;

/// <summary>Load of the whole computer, as the UI's performance tiles show it ("whole computer").</summary>
/// <param name="CpuPercent">Busy share of all cores since the previous sample, 0..100 (null when unknown).</param>
/// <param name="UsedMemoryBytes">Physical memory in use by everything (null when unknown).</param>
/// <param name="TotalMemoryBytes">Installed physical memory (null when unknown).</param>
public readonly record struct SystemLoad(double? CpuPercent, long? UsedMemoryBytes, long? TotalMemoryBytes);

/// <summary>Samples the machine's CPU and memory load. Platform projects implement it; see <see cref="ProcessMetrics"/>.</summary>
public interface ISystemMetrics
{
    /// <summary>The load since the previous call (the first call measures since construction).</summary>
    SystemLoad Sample();
}

/// <summary>
/// Fallback for platforms without an implementation: this process's CPU share and working set (not the machine's),
/// against the memory the runtime can see.
/// </summary>
public sealed class ProcessMetrics : ISystemMetrics
{
    private readonly Lock _lock = new();
    private long _lastTicks = Stopwatch.GetTimestamp();
    private TimeSpan _lastCpu = Process.GetCurrentProcess().TotalProcessorTime;

    public SystemLoad Sample()
    {
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        double percent;
        lock (_lock)
        {
            var now = Stopwatch.GetTimestamp();
            var seconds = Math.Max(Stopwatch.GetElapsedTime(_lastTicks, now).TotalSeconds, 0.001);
            percent = (cpu - _lastCpu).TotalSeconds / seconds / Environment.ProcessorCount * 100;
            _lastTicks = now;
            _lastCpu = cpu;
        }

        var total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return new SystemLoad(Math.Clamp(percent, 0, 100), process.WorkingSet64, total > 0 ? total : null);
    }
}
