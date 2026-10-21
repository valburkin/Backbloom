using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Backbloom.Engine.Platform;

namespace Backbloom.Platform.Windows;

/// <summary>
/// GPU load from the Windows performance counters Task Manager uses (PDH, WDDM 2.x): <c>GPU Engine(*)\Utilization
/// Percentage</c> per process and engine, <c>GPU Adapter Memory(*)\Dedicated Usage</c> and <c>GPU Process
/// Memory(*)\Dedicated Usage</c> / <c>Shared Usage</c>. The query is opened on the first sample (about 140 ms, so not
/// on the app's start); a sample then takes about 1 ms. Instances of processes that start later are included (the
/// wildcards are expanded on every collection).
/// </summary>
/// <remarks>
/// With hundreds of instances, a sample reads the names in place from one reused buffer and keeps a total per engine
/// (adapter, physical GPU, engine: a few dozen) between samples, so it hardly allocates: the recorder's allocation and
/// garbage collection numbers describe Backbloom, not its measuring.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsGpuMetrics : IGpuMetrics, IDisposable
{
    private const uint FormatDouble = 0x00000200;
    private const uint FormatNoCap100 = 0x00008000;
    private const uint PdhMoreData = 0x800007D2;

    private readonly Lock _lock = new();
    private readonly string _ownPrefix = string.Create(CultureInfo.InvariantCulture, $"pid_{Environment.ProcessId}_");
    private readonly EngineTotals _totals = new();
    private byte[] _buffer = new byte[64 * 1024];
    private nint _query;
    private nint _engines;
    private nint _adapterMemory;
    private nint _processDedicated;
    private nint _processShared;
    private bool _opened;
    private bool _unavailable;

    public GpuLoad Sample()
    {
        lock (_lock)
        {
            if (!Open() || PdhCollectQueryData(_query) != 0)
            {
                return default;
            }

            // the utilization is a rate: unknown on the first collection
            _totals.Clear();
            var (computer, app) = Read(_engines, _totals.Add) ? _totals.Result() : (null, null);
            return new GpuLoad(computer, app, Sum(_adapterMemory, null), Sum(_processDedicated, _ownPrefix), Sum(_processShared, _ownPrefix));
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_query != 0)
            {
                _ = PdhCloseQuery(_query);
                _query = 0;
            }

            _unavailable = true;
        }
    }

    /// <summary>
    /// The busiest engine's utilization, for all processes (summed per engine) and for the processes whose instance
    /// names start with <paramref name="ownPrefix"/>. Instance names: <c>pid_1234_luid_0x0_0x14991_phys_0_eng_0_engtype_3D</c>.
    /// </summary>
    internal static (double? Computer, double? App) Utilization(IReadOnlyList<(string Name, double Value)> engines, string ownPrefix)
    {
        var totals = new EngineTotals();
        foreach (var (name, value) in engines)
        {
            totals.Add(name, value, name.StartsWith(ownPrefix, StringComparison.OrdinalIgnoreCase));
        }

        return totals.Result();
    }

    private long? Sum(nint counter, string? prefix)
    {
        double sum = 0;
        return Read(counter, (name, value, own) => sum += prefix is null || own ? value : 0) ? (long)sum : null;
    }

    // Runs under _lock; false when the counters aren't there (older Windows or drivers): then never again.
    private bool Open()
    {
        if (_opened || _unavailable)
        {
            return _opened;
        }

        if (PdhOpenQueryW(null, 0, out _query) != 0)
        {
            _unavailable = true;
            return false;
        }

        if (PdhAddEnglishCounterW(_query, @"\GPU Engine(*)\Utilization Percentage", 0, out _engines) != 0
            || PdhAddEnglishCounterW(_query, @"\GPU Adapter Memory(*)\Dedicated Usage", 0, out _adapterMemory) != 0
            || PdhAddEnglishCounterW(_query, @"\GPU Process Memory(*)\Dedicated Usage", 0, out _processDedicated) != 0
            || PdhAddEnglishCounterW(_query, @"\GPU Process Memory(*)\Shared Usage", 0, out _processShared) != 0)
        {
            _ = PdhCloseQuery(_query);
            _query = 0;
            _unavailable = true;
            return false;
        }

        _opened = true;
        return true;
    }

    // Hands each valid instance of the counter (its name read in place, its value, whether it is this process's) to
    // `item`; false when the counter has no values yet. Runs under _lock.
    private unsafe bool Read(nint counter, ItemHandler item)
    {
        uint size = 0;
        if (PdhGetFormattedCounterArrayW(counter, FormatDouble | FormatNoCap100, ref size, out _, null) != PdhMoreData || size == 0)
        {
            return false;
        }

        if (_buffer.Length < size)
        {
            _buffer = new byte[Math.Max(size, _buffer.Length * 2)];
        }

        fixed (byte* start = _buffer)
        {
            if (PdhGetFormattedCounterArrayW(counter, FormatDouble | FormatNoCap100, ref size, out var count, start) != 0)
            {
                return false;
            }

            var items = (CounterItem*)start;
            for (var i = 0; i < count; i++)
            {
                // 0: valid, 1: new data; anything else (e.g. a process that just ended) is left out
                if (items[i].Status <= 1)
                {
                    var name = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(items[i].Name);
                    item(name, items[i].Value, name.StartsWith(_ownPrefix, StringComparison.OrdinalIgnoreCase));
                }
            }
        }

        return true;
    }

    private delegate void ItemHandler(ReadOnlySpan<char> name, double value, bool own);

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhOpenQueryW(string? dataSource, nint userData, out nint query);

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhAddEnglishCounterW(nint query, string path, nint userData, out nint counter);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll")]
    private static unsafe partial uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint bufferSize, out uint itemCount, byte* items);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCloseQuery(nint query);

    // PDH_FMT_COUNTERVALUE_ITEM_W with a double value (24 bytes on x64, the only platform the app ships for)
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct CounterItem
    {
        public char* Name;
        public uint Status;
        public double Value;
    }

    /// <summary>
    /// Utilization per engine (the instance name from <c>_luid_</c> on: adapter, physical GPU, engine), for all
    /// processes and for this one. The keys stay between samples (a string only for an engine not seen before).
    /// </summary>
    private sealed class EngineTotals
    {
        private readonly Dictionary<string, (double All, double Own)> _engines = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (double All, double Own)>.AlternateLookup<ReadOnlySpan<char>> _lookup;
        private readonly List<string> _keys = [];
        private bool _any;

        public EngineTotals() => _lookup = _engines.GetAlternateLookup<ReadOnlySpan<char>>();

        public void Clear()
        {
            foreach (var key in _keys)
            {
                CollectionsMarshal.GetValueRefOrNullRef(_engines, key) = default;
            }

            _any = false;
        }

        public void Add(ReadOnlySpan<char> name, double value, bool own)
        {
            var luid = name.IndexOf("_luid_", StringComparison.OrdinalIgnoreCase);
            if (luid < 0)
            {
                return;
            }

            ref var total = ref CollectionsMarshal.GetValueRefOrAddDefault(_lookup, name[luid..], out var exists);
            total = (total.All + value, total.Own + (own ? value : 0));
            if (!exists)
            {
                _keys.Add(name[luid..].ToString());
            }

            _any = true;
        }

        public (double? Computer, double? App) Result()
        {
            if (!_any)
            {
                return (null, null);
            }

            double all = 0, own = 0;
            foreach (var (engineAll, engineOwn) in _engines.Values)
            {
                all = Math.Max(all, engineAll);
                own = Math.Max(own, engineOwn);
            }

            return (Math.Clamp(all, 0, 100), Math.Clamp(own, 0, 100));
        }
    }
}
