namespace Overglow.Devices;

/// <summary>Counters for one device, updated lock-free from the pipeline and device threads.</summary>
public sealed class FrameStats
{
    private long _submitted;
    private long _written;
    private long _dropped;
    private long _writeErrors;

    /// <summary>Frames handed to the device by the pipeline.</summary>
    public long Submitted => Interlocked.Read(ref _submitted);

    /// <summary>Frames sent to the controller (including refreshes and blink frames).</summary>
    public long Written => Interlocked.Read(ref _written);

    /// <summary>Frames replaced by a newer one before the device thread got to send them.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    public long WriteErrors => Interlocked.Read(ref _writeErrors);

    public FrameStatsSnapshot Snapshot() => new(Submitted, Written, Dropped, WriteErrors);

    internal void OnSubmitted() => Interlocked.Increment(ref _submitted);

    internal void OnWritten() => Interlocked.Increment(ref _written);

    internal void OnDropped() => Interlocked.Increment(ref _dropped);

    internal void OnWriteError() => Interlocked.Increment(ref _writeErrors);
}

/// <summary>Totals at one moment; rates are computed by whoever samples them periodically.</summary>
public readonly record struct FrameStatsSnapshot(long Submitted, long Written, long Dropped, long WriteErrors);
