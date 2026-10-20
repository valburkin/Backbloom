using System.Diagnostics;

namespace Overglow.Engine.Platform;

/// <summary>
/// The monotonic clock of the lighting: time stamps for smoothing and the muxer, and waiting for the next smoothing
/// tick. Injected so tests can drive time; the Windows build uses a high-resolution waitable timer.
/// </summary>
public interface IClock
{
    /// <summary>Milliseconds since an arbitrary start, never going backwards.</summary>
    long NowMilliseconds { get; }

    /// <summary>
    /// Blocks the calling thread until <see cref="NowMilliseconds"/> reaches <paramref name="dueMilliseconds"/> (or
    /// a little later). Meant for dedicated threads; must not allocate after the first call on a thread.
    /// </summary>
    void SleepUntil(long dueMilliseconds);
}

/// <summary>
/// Portable clock: <see cref="Stopwatch"/> time and <see cref="Thread.Sleep(int)"/>, whose resolution is the OS timer
/// tick (15.6 ms on Windows by default), so a 20 ms tick lands on 15.6 / 31.2 ms steps; the average rate stays exact
/// because the callers wait for absolute deadlines.
/// </summary>
public class SystemClock : IClock
{
    private readonly long _start = Stopwatch.GetTimestamp();

    public long NowMilliseconds => (long)Stopwatch.GetElapsedTime(_start).TotalMilliseconds;

    public virtual void SleepUntil(long dueMilliseconds)
    {
        var wait = dueMilliseconds - NowMilliseconds;
        if (wait > 0)
        {
            Thread.Sleep((int)Math.Min(wait, int.MaxValue));
        }
    }
}
