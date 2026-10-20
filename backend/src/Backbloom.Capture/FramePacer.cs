using System.Diagnostics;

namespace Backbloom.Capture;

/// <summary>
/// Paces a loop to a target rate using absolute deadlines, so the average rate stays on target even when the OS
/// timer is coarse (15.6 ms on Windows by default): a late wake-up shortens the next wait instead of adding up.
/// </summary>
public sealed class FramePacer
{
    private readonly long _period;
    private long _next;

    public FramePacer(double framesPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(framesPerSecond);
        _period = (long)(Stopwatch.Frequency / framesPerSecond);
        _next = Stopwatch.GetTimestamp();
    }

    public double FramesPerSecond => (double)Stopwatch.Frequency / _period;

    /// <summary>Waits until the next frame is due. Returns false when canceled.</summary>
    public bool WaitNext(CancellationToken cancellationToken)
    {
        _next += _period;
        var now = Stopwatch.GetTimestamp();
        if (now - _next > 2 * _period)
        {
            // Far behind (debugger, sleep): skip the missed frames instead of bursting through them.
            _next = now + _period;
        }

        // Waits of less than a millisecond are skipped: the OS would round them up to a whole timer tick.
        var ms = (int)((_next - now) * 1000 / Stopwatch.Frequency);
        if (ms > 0 && cancellationToken.WaitHandle.WaitOne(ms))
        {
            return false;
        }

        return !cancellationToken.IsCancellationRequested;
    }
}
