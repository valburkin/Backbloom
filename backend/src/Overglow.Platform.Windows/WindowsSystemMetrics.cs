using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Overglow.Engine.Platform;

namespace Overglow.Platform.Windows;

/// <summary>
/// Machine-wide CPU load (<c>GetSystemTimes</c>: busy share of all cores between two samples) and physical memory in
/// use (<c>GlobalMemoryStatusEx</c>), the numbers HyperHDR's SystemPerformanceCounters reported (it used the PDH
/// "% Processor Time" counter, which is the same quantity).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsSystemMetrics : ISystemMetrics
{
    private readonly Lock _lock = new();
    private ulong _lastIdle;
    private ulong _lastTotal;
    private double? _lastPercent;

    public WindowsSystemMetrics()
    {
        if (TryGetTimes(out var idle, out var total))
        {
            _lastIdle = idle;
            _lastTotal = total;
        }
    }

    public SystemLoad Sample()
    {
        double? cpu = null;
        if (TryGetTimes(out var idle, out var total))
        {
            lock (_lock)
            {
                // the counters advance in timer ticks: within one tick, report the previous value
                var totalDelta = total - _lastTotal;
                if (totalDelta > 0)
                {
                    _lastPercent = Math.Clamp(100.0 * (totalDelta - (idle - _lastIdle)) / totalDelta, 0, 100);
                    _lastIdle = idle;
                    _lastTotal = total;
                }

                cpu = _lastPercent;
            }
        }

        var memory = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref memory))
        {
            return new SystemLoad(cpu, null, null);
        }

        return new SystemLoad(cpu, (long)(memory.TotalPhys - memory.AvailPhys), (long)memory.TotalPhys);
    }

    // kernel time includes idle time
    private static bool TryGetTimes(out ulong idle, out ulong total)
    {
        if (GetSystemTimes(out var i, out var k, out var u))
        {
            idle = i;
            total = k + u;
            return true;
        }

        idle = total = 0;
        return false;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }
}
