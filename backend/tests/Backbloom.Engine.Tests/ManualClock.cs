using Backbloom.Engine.Platform;

namespace Backbloom.Engine.Tests;

/// <summary>A clock that only moves when told to; waits return at once.</summary>
internal sealed class ManualClock(long start = 1000) : IClock
{
    private long _now = start;

    public long NowMilliseconds => Interlocked.Read(ref _now);

    public void Advance(long milliseconds) => Interlocked.Add(ref _now, milliseconds);

    public void SleepUntil(long dueMilliseconds)
    {
    }
}
