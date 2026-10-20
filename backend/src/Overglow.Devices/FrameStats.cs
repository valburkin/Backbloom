using System.Diagnostics;

namespace Overglow.Devices;

/// <summary>Counters for one device, updated lock-free from the pipeline and device threads.</summary>
public sealed class FrameStats
{
    private long _submitted;
    private long _written;
    private long _dropped;
    private long _writeErrors;
    private long _latencyTicks;
    private long _latencySamples;

    /// <summary>Frames handed to the device by the pipeline.</summary>
    public long Submitted => Interlocked.Read(ref _submitted);

    /// <summary>Frames sent to the controller (including refreshes and blink frames).</summary>
    public long Written => Interlocked.Read(ref _written);

    /// <summary>Frames replaced by a newer one before the device thread got to send them.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    public long WriteErrors => Interlocked.Read(ref _writeErrors);

    public FrameStatsSnapshot Snapshot() =>
        new(Submitted, Written, Dropped, WriteErrors, Interlocked.Read(ref _latencyTicks), Interlocked.Read(ref _latencySamples));

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
}

/// <summary>Totals at one moment; rates are computed by whoever samples them periodically.</summary>
/// <param name="LatencyTicks">Sum over new frames of the time from <c>Submit</c> until the driver had written them (Stopwatch ticks).</param>
/// <param name="LatencySamples">Frames counted in <paramref name="LatencyTicks"/>.</param>
public readonly record struct FrameStatsSnapshot(long Submitted, long Written, long Dropped, long WriteErrors, long LatencyTicks = 0, long LatencySamples = 0)
{
    /// <summary>Average submit-to-written time between an earlier snapshot and this one, in milliseconds (null: no new frames).</summary>
    public double? AverageLatencyMsSince(FrameStatsSnapshot earlier)
    {
        var samples = LatencySamples - earlier.LatencySamples;
        return samples > 0 ? (LatencyTicks - earlier.LatencyTicks) * 1000.0 / Stopwatch.Frequency / samples : null;
    }
}
