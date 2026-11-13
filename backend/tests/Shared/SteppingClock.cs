using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;

namespace Backbloom.Tests.Time;

/// <summary>
/// A fake clock that remembers the timers code creates on it, so a test can run code that waits on the clock
/// (<c>Task.Delay(span, time)</c>) through each of its waits exactly, without real time passing and without skipping
/// past a wait the code hasn't started yet.
/// </summary>
public sealed class SteppingClock : FakeTimeProvider
{
    private int _created;
    private long _lastDueTicks;

    /// <summary>How many timers were created on this clock so far.</summary>
    public int TimersCreated => Volatile.Read(ref _created);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        Interlocked.Exchange(ref _lastDueTicks, dueTime.Ticks);
        Interlocked.Increment(ref _created);
        return timer;
    }

    /// <summary>
    /// Runs <paramref name="task"/> to completion: whenever it creates a timer on this clock, advances the clock by
    /// that timer's due time.
    /// </summary>
    public async Task<T> RunAsync<T>(Task<T> task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var handled = 0;
        var stall = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            var created = TimersCreated;
            if (created == handled)
            {
                if (stall.Elapsed > TimeSpan.FromSeconds(30))
                {
                    throw new TimeoutException("The task neither completed nor waited on the clock.");
                }

                await Task.Yield();
                continue;
            }

            handled = created;
            Advance(TimeSpan.FromTicks(Interlocked.Read(ref _lastDueTicks)));
            stall.Restart();
        }

        return await task;
    }

    /// <inheritdoc cref="RunAsync{T}(Task{T})"/>
    public Task RunAsync(Task task) => RunAsync(Completion(task));

    private static async Task<bool> Completion(Task task)
    {
        await task;
        return true;
    }
}
