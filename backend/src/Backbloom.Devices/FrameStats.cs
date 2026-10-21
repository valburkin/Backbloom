using System.Diagnostics;
using System.Numerics;

namespace Backbloom.Devices;

/// <summary>Counters for one device, updated lock-free from the pipeline and device threads.</summary>
public sealed class FrameStats
{
    /// <summary>One million: a channel's <c>Millionths</c> per frame at full brightness.</summary>
    public const long Full = 1_000_000;

    private long _submitted;
    private long _written;
    private long _dropped;
    private long _writeErrors;
    private long _latencyTicks;
    private long _latencySamples;
    private long _redMillionths;
    private long _greenMillionths;
    private long _blueMillionths;
    private long _loadFrames;
    private long _writeTicks;
    private long _writeCalls;
    private long _peakLoadMillionths;
    private long _peakLedMillionths;
    private long _peakWriteTicks;

    /// <summary>Frames handed to the device by the pipeline.</summary>
    public long Submitted => Interlocked.Read(ref _submitted);

    /// <summary>Frames sent to the controller (including refreshes and blink frames).</summary>
    public long Written => Interlocked.Read(ref _written);

    /// <summary>Frames replaced by a newer one before the device thread got to send them, while the device was ready.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    public long WriteErrors => Interlocked.Read(ref _writeErrors);

    public FrameStatsSnapshot Snapshot() =>
        new(Submitted, Written, Dropped, WriteErrors, Interlocked.Read(ref _latencyTicks), Interlocked.Read(ref _latencySamples))
        {
            RedMillionths = Interlocked.Read(ref _redMillionths),
            GreenMillionths = Interlocked.Read(ref _greenMillionths),
            BlueMillionths = Interlocked.Read(ref _blueMillionths),
            LoadFrames = Interlocked.Read(ref _loadFrames),
            WriteTicks = Interlocked.Read(ref _writeTicks),
            WriteCalls = Interlocked.Read(ref _writeCalls),
        };

    /// <summary>
    /// The highest values of one frame since the previous call, which resets them: the performance recorder takes
    /// them once per sample (through the engine, so nothing else can).
    /// </summary>
    public FramePeaks TakePeaks() => new(
        Interlocked.Exchange(ref _peakLoadMillionths, 0) / (double)Full,
        Interlocked.Exchange(ref _peakLedMillionths, 0) / (double)Full,
        Interlocked.Exchange(ref _peakWriteTicks, 0) * 1000.0 / Stopwatch.Frequency);

    /// <summary>
    /// A frame was sent: <paramref name="channels"/> are the means of its red, green and blue channels and
    /// <paramref name="brightestLed"/> the highest mean of one LED's channels (all 0..1, shares of full white).
    /// </summary>
    internal void OnSent(Vector3 channels, double brightestLed)
    {
        var red = Millionths(channels.X);
        var green = Millionths(channels.Y);
        var blue = Millionths(channels.Z);
        Interlocked.Add(ref _redMillionths, red);
        Interlocked.Add(ref _greenMillionths, green);
        Interlocked.Add(ref _blueMillionths, blue);
        Interlocked.Increment(ref _loadFrames);
        RaisePeak(ref _peakLoadMillionths, (red + green + blue) / 3);
        RaisePeak(ref _peakLedMillionths, Millionths(brightestLed));
    }

    /// <summary>The driver took <paramref name="ticks"/> (<see cref="Stopwatch"/> ticks) to send a frame.</summary>
    internal void OnWriteTime(long ticks)
    {
        Interlocked.Add(ref _writeTicks, ticks);
        Interlocked.Increment(ref _writeCalls);
        RaisePeak(ref _peakWriteTicks, ticks);
    }

    internal void OnSubmitted() => Interlocked.Increment(ref _submitted);

    internal void OnWritten() => Interlocked.Increment(ref _written);

    internal void OnDropped() => Interlocked.Increment(ref _dropped);

    internal void OnWriteError() => Interlocked.Increment(ref _writeErrors);

    /// <summary>A new frame was written <paramref name="ticks"/> (<see cref="Stopwatch"/> ticks) after it was submitted.</summary>
    internal void OnLatency(long ticks)
    {
        Interlocked.Add(ref _latencyTicks, ticks);
        Interlocked.Increment(ref _latencySamples);
    }

    private static long Millionths(double share) => (long)(Math.Clamp(share, 0, 1) * Full);

    private static void RaisePeak(ref long peak, long value)
    {
        var seen = Interlocked.Read(ref peak);
        while (value > seen)
        {
            var before = Interlocked.CompareExchange(ref peak, value, seen);
            if (before == seen)
            {
                break;
            }

            seen = before;
        }
    }
}

/// <summary>The highest values of one frame in a period (see <see cref="FrameStats.TakePeaks"/>).</summary>
/// <param name="Load">Mean of all channels, 0..1 (share of full white).</param>
/// <param name="Led">Mean of one LED's channels, the brightest LED, 0..1.</param>
/// <param name="WriteMs">Time the driver took to send one frame.</param>
public readonly record struct FramePeaks(double Load, double Led, double WriteMs);

/// <summary>Totals at one moment; rates are computed by whoever samples them periodically.</summary>
/// <param name="LatencyTicks">Sum over new frames of the time from <c>Submit</c> until the driver had written them (Stopwatch ticks).</param>
/// <param name="LatencySamples">Frames counted in <paramref name="LatencyTicks"/>.</param>
public readonly record struct FrameStatsSnapshot(long Submitted, long Written, long Dropped, long WriteErrors, long LatencyTicks = 0, long LatencySamples = 0)
{
    /// <summary>Sums over sent frames of their channels' means (<see cref="FrameStats.Full"/> = full brightness).</summary>
    public long RedMillionths { get; init; }

    public long GreenMillionths { get; init; }

    public long BlueMillionths { get; init; }

    /// <summary>Frames counted in the channel sums.</summary>
    public long LoadFrames { get; init; }

    /// <summary>Sum of the time the driver took to send frames (Stopwatch ticks), over <see cref="WriteCalls"/> frames.</summary>
    public long WriteTicks { get; init; }

    public long WriteCalls { get; init; }

    /// <summary>Average red, green and blue of the frames sent since an earlier snapshot, 0..1 (null: none sent).</summary>
    public Vector3? AverageChannelsSince(FrameStatsSnapshot earlier)
    {
        var frames = LoadFrames - earlier.LoadFrames;
        return frames > 0
            ? new Vector3(RedMillionths - earlier.RedMillionths, GreenMillionths - earlier.GreenMillionths, BlueMillionths - earlier.BlueMillionths) / (FrameStats.Full * (float)frames)
            : null;
    }

    /// <summary>Average load (mean of all channels, share of full white) of the frames sent since an earlier snapshot (null: none sent).</summary>
    public double? AverageLoadSince(FrameStatsSnapshot earlier)
    {
        var frames = LoadFrames - earlier.LoadFrames;
        return frames > 0
            ? (RedMillionths - earlier.RedMillionths + GreenMillionths - earlier.GreenMillionths + BlueMillionths - earlier.BlueMillionths) / 3.0 / FrameStats.Full / frames
            : null;
    }

    /// <summary>Average time the driver took to send a frame since an earlier snapshot, in milliseconds (null: none sent).</summary>
    public double? AverageWriteMsSince(FrameStatsSnapshot earlier)
    {
        var calls = WriteCalls - earlier.WriteCalls;
        return calls > 0 ? (WriteTicks - earlier.WriteTicks) * 1000.0 / Stopwatch.Frequency / calls : null;
    }

    /// <summary>Average submit-to-written time between an earlier snapshot and this one, in milliseconds (null: no new frames).</summary>
    public double? AverageLatencyMsSince(FrameStatsSnapshot earlier)
    {
        var samples = LatencySamples - earlier.LatencySamples;
        return samples > 0 ? (LatencyTicks - earlier.LatencyTicks) * 1000.0 / Stopwatch.Frequency / samples : null;
    }
}
